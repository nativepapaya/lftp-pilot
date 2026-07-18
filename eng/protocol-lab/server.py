"""Disposable loopback FTP-family and SFTP servers for LFTP Pilot acceptance.

This process is deliberately test-only. It binds every listener to loopback,
generates fresh identities in the caller-provided root, and exits when stdin is
closed or contains a line. No generated key or credential belongs in source.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import ipaddress
import json
import os
import pathlib
import posixpath
import shutil
import socket
import stat
import sys
import threading
import time
from typing import Any

import paramiko
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler, TLS_FTPHandler
from pyftpdlib.ioloop import IOLoop
from pyftpdlib.log import config_logging
from pyftpdlib.servers import FTPServer


LAB_USER = "lftp-pilot"
LAB_PASSWORD = "loopback-only-password"
LAB_KEY_PASSPHRASE = "loopback-only-key-passphrase"
SEED_NAME = "seed-雪.txt"
SEED_CONTENT = "LFTP Pilot loopback protocol fixture\n"


def _write_tls_identity(root: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "LFTP Pilot Loopback Lab")])
    now = dt.datetime.now(dt.timezone.utc)
    certificate = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(minutes=5))
        .not_valid_after(now + dt.timedelta(days=2))
        .add_extension(
            x509.SubjectAlternativeName([
                x509.DNSName("localhost"),
                x509.IPAddress(ipaddress.ip_address("127.0.0.1")),
            ]),
            critical=False,
        )
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(
            x509.KeyUsage(
                digital_signature=True,
                content_commitment=False,
                key_encipherment=True,
                data_encipherment=False,
                key_agreement=False,
                key_cert_sign=True,
                crl_sign=True,
                encipher_only=None,
                decipher_only=None,
            ),
            critical=True,
        )
        .sign(key, hashes.SHA256())
    )
    certificate_path = root / "loopback-ca.pem"
    key_path = root / "loopback-tls-key.pem"
    certificate_path.write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
    key_path.write_bytes(
        key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.TraditionalOpenSSL,
            serialization.NoEncryption(),
        )
    )
    return certificate_path, key_path


def _seed(root: pathlib.Path) -> None:
    root.mkdir(parents=True, exist_ok=True)
    (root / SEED_NAME).write_text(SEED_CONTENT, encoding="utf-8")
    nested = root / "existing-folder"
    nested.mkdir()
    (nested / "nested.txt").write_text("nested fixture\n", encoding="utf-8")


class ImplicitTLSFTPHandler(TLS_FTPHandler):
    """TLS-wrap the control channel before pyftpdlib writes its greeting."""

    tls_control_required = True
    tls_data_required = True

    def handle(self) -> None:
        self.secure_connection(self.ssl_context)
        super().handle()


class FtpEndpoint:
    def __init__(
        self,
        root: pathlib.Path,
        handler_base: type[FTPHandler],
        certificate_path: pathlib.Path | None = None,
        key_path: pathlib.Path | None = None,
        require_tls: bool = False,
    ) -> None:
        authorizer = DummyAuthorizer()
        authorizer.add_user(LAB_USER, LAB_PASSWORD, str(root), perm="elradfmwMT")
        attributes: dict[str, Any] = {
            "authorizer": authorizer,
            "banner": "LFTP Pilot controlled loopback lab",
            "permit_foreign_addresses": False,
            "permit_privileged_ports": False,
            "timeout": 30,
        }
        if issubclass(handler_base, TLS_FTPHandler):
            attributes.update({
                "certfile": str(certificate_path),
                "keyfile": str(key_path),
                "tls_control_required": require_tls,
                "tls_data_required": require_tls,
                # A distinct context is required for every generated identity.
                "ssl_context": None,
            })
        handler = type(f"Lab{handler_base.__name__}", (handler_base,), attributes)
        self._ioloop = IOLoop()
        self._server = FTPServer(("127.0.0.1", 0), handler, ioloop=self._ioloop)
        self.port = int(self._server.socket.getsockname()[1])
        self._thread = threading.Thread(
            target=self._server.serve_forever,
            kwargs={"timeout": 0.1, "blocking": True, "handle_exit": False},
            name=f"ftp-lab-{self.port}",
            daemon=True,
        )

    def start(self) -> None:
        self._thread.start()

    def close(self) -> None:
        self._server.close_all()
        self._ioloop.close()
        self._thread.join(timeout=2)


class LabSshServer(paramiko.ServerInterface):
    def __init__(self, authorized_key: paramiko.PKey) -> None:
        self._authorized_key = authorized_key

    def get_allowed_auths(self, username: str) -> str:
        return "publickey,password" if username == LAB_USER else ""

    def check_auth_password(self, username: str, password: str) -> int:
        return (
            paramiko.AUTH_SUCCESSFUL
            if username == LAB_USER and password == LAB_PASSWORD
            else paramiko.AUTH_FAILED
        )

    def check_auth_publickey(self, username: str, key: paramiko.PKey) -> int:
        return (
            paramiko.AUTH_SUCCESSFUL
            if username == LAB_USER and key == self._authorized_key
            else paramiko.AUTH_FAILED
        )

    def check_channel_request(self, kind: str, chanid: int) -> int:
        del chanid
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


class RootedSftpServer(paramiko.SFTPServerInterface):
    def __init__(self, server: paramiko.ServerInterface, *args: Any, root: str, **kwargs: Any) -> None:
        super().__init__(server, *args, **kwargs)
        self._root = pathlib.Path(root).resolve()

    def _local(self, remote_path: str) -> pathlib.Path:
        normalized = posixpath.normpath("/" + remote_path.lstrip("/"))
        parts = [part for part in normalized.split("/") if part]
        candidate = self._root.joinpath(*parts).resolve()
        if os.path.commonpath([str(self._root), str(candidate)]) != str(self._root):
            raise PermissionError("SFTP path escaped the lab root")
        return candidate

    @staticmethod
    def _error(exception: OSError) -> int:
        return paramiko.SFTPServer.convert_errno(exception.errno or 1)

    def canonicalize(self, path: str) -> str:
        normalized = posixpath.normpath("/" + path.lstrip("/"))
        self._local(normalized)
        return normalized

    def list_folder(self, path: str) -> list[paramiko.SFTPAttributes] | int:
        try:
            local = self._local(path)
            entries: list[paramiko.SFTPAttributes] = []
            for name in os.listdir(local):
                attributes = paramiko.SFTPAttributes.from_stat(os.lstat(local / name))
                attributes.filename = name
                entries.append(attributes)
            return entries
        except OSError as exception:
            return self._error(exception)

    def stat(self, path: str) -> paramiko.SFTPAttributes | int:
        try:
            return paramiko.SFTPAttributes.from_stat(os.stat(self._local(path)))
        except OSError as exception:
            return self._error(exception)

    def lstat(self, path: str) -> paramiko.SFTPAttributes | int:
        try:
            return paramiko.SFTPAttributes.from_stat(os.lstat(self._local(path)))
        except OSError as exception:
            return self._error(exception)

    def open(self, path: str, flags: int, attr: paramiko.SFTPAttributes) -> paramiko.SFTPHandle | int:
        try:
            local = self._local(path)
            mode = attr.st_mode if attr.st_mode is not None else 0o666
            descriptor = os.open(local, flags, mode)
            if flags & os.O_WRONLY:
                file_mode = "ab" if flags & os.O_APPEND else "wb"
            elif flags & os.O_RDWR:
                file_mode = "a+b" if flags & os.O_APPEND else "r+b"
            else:
                file_mode = "rb"
            file_object = os.fdopen(descriptor, file_mode, buffering=0)
            handle = paramiko.SFTPHandle(flags)
            if flags & os.O_WRONLY:
                handle.writefile = file_object
            elif flags & os.O_RDWR:
                handle.readfile = file_object
                handle.writefile = file_object
            else:
                handle.readfile = file_object
            return handle
        except OSError as exception:
            return self._error(exception)

    def remove(self, path: str) -> int:
        try:
            os.remove(self._local(path))
            return paramiko.SFTP_OK
        except OSError as exception:
            return self._error(exception)

    def rename(self, oldpath: str, newpath: str) -> int:
        try:
            os.rename(self._local(oldpath), self._local(newpath))
            return paramiko.SFTP_OK
        except OSError as exception:
            return self._error(exception)

    def posix_rename(self, oldpath: str, newpath: str) -> int:
        try:
            os.replace(self._local(oldpath), self._local(newpath))
            return paramiko.SFTP_OK
        except OSError as exception:
            return self._error(exception)

    def mkdir(self, path: str, attr: paramiko.SFTPAttributes) -> int:
        try:
            mode = attr.st_mode if attr.st_mode is not None else 0o777
            os.mkdir(self._local(path), mode)
            return paramiko.SFTP_OK
        except OSError as exception:
            return self._error(exception)

    def rmdir(self, path: str) -> int:
        try:
            os.rmdir(self._local(path))
            return paramiko.SFTP_OK
        except OSError as exception:
            return self._error(exception)

    def chattr(self, path: str, attr: paramiko.SFTPAttributes) -> int:
        try:
            paramiko.SFTPServer.set_file_attr(str(self._local(path)), attr)
            return paramiko.SFTP_OK
        except OSError as exception:
            return self._error(exception)


class SftpEndpoint:
    def __init__(self, root: pathlib.Path, identity_root: pathlib.Path) -> None:
        self._root = root
        self._host_key_lock = threading.Lock()
        self._host_key = paramiko.RSAKey.generate(2048)
        self._client_key = paramiko.RSAKey.generate(2048)
        self.client_key_path = identity_root / "sftp-client-key"
        self.encrypted_client_key_path = identity_root / "sftp-client-key-encrypted"
        self._client_key.write_private_key_file(str(self.client_key_path))
        self._client_key.write_private_key_file(str(self.encrypted_client_key_path), password=LAB_KEY_PASSPHRASE)
        self._listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._listener.bind(("127.0.0.1", 0))
        self._listener.listen(16)
        self._listener.settimeout(0.25)
        self.port = int(self._listener.getsockname()[1])
        self._stopping = threading.Event()
        self._transports: list[paramiko.Transport] = []
        self._lock = threading.Lock()
        self._thread = threading.Thread(target=self._accept, name=f"sftp-lab-{self.port}", daemon=True)

    @property
    def host_key_algorithm(self) -> str:
        with self._host_key_lock:
            return self._host_key.get_name()

    @property
    def host_key_base64(self) -> str:
        with self._host_key_lock:
            return self._host_key.get_base64()

    @property
    def host_key_fingerprint(self) -> str:
        digest = hashlib.sha256(base64.b64decode(self.host_key_base64)).digest()
        return "SHA256:" + base64.b64encode(digest).decode("ascii").rstrip("=")

    def rotate_host_key(self) -> None:
        replacement = paramiko.RSAKey.generate(2048)
        with self._host_key_lock:
            self._host_key = replacement

    def start(self) -> None:
        self._thread.start()

    def _accept(self) -> None:
        while not self._stopping.is_set():
            try:
                client, _ = self._listener.accept()
            except TimeoutError:
                continue
            except OSError:
                break
            threading.Thread(target=self._serve, args=(client,), daemon=True).start()

    def _serve(self, client: socket.socket) -> None:
        transport = paramiko.Transport(client)
        with self._lock:
            self._transports.append(transport)
        try:
            with self._host_key_lock:
                host_key = self._host_key
            transport.add_server_key(host_key)
            transport.set_subsystem_handler(
                "sftp", paramiko.SFTPServer, RootedSftpServer, root=str(self._root)
            )
            transport.start_server(server=LabSshServer(self._client_key))
            while transport.is_active() and not self._stopping.wait(0.1):
                pass
        except (EOFError, OSError, paramiko.SSHException):
            pass
        finally:
            transport.close()
            with self._lock:
                if transport in self._transports:
                    self._transports.remove(transport)

    def close(self) -> None:
        self._stopping.set()
        self._listener.close()
        with self._lock:
            for transport in self._transports:
                transport.close()
        self._thread.join(timeout=2)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=pathlib.Path)
    parser.add_argument("--config", required=True, type=pathlib.Path)
    parser.add_argument("--stop-file", type=pathlib.Path)
    arguments = parser.parse_args()
    root = arguments.root.resolve()
    if root.exists():
        shutil.rmtree(root)
    root.mkdir(parents=True)
    config_logging(level="ERROR")

    certificate_path, tls_key_path = _write_tls_identity(root)
    endpoint_roots: dict[str, pathlib.Path] = {}
    for name in ["ftp", "ftp_opportunistic_tls", "ftps_explicit", "ftps_implicit", "sftp"]:
        endpoint_root = root / "roots" / name
        _seed(endpoint_root)
        endpoint_roots[name] = endpoint_root

    endpoints: list[Any] = [
        FtpEndpoint(endpoint_roots["ftp"], FTPHandler),
        FtpEndpoint(endpoint_roots["ftp_opportunistic_tls"], TLS_FTPHandler, certificate_path, tls_key_path),
        FtpEndpoint(endpoint_roots["ftps_explicit"], TLS_FTPHandler, certificate_path, tls_key_path, require_tls=True),
        FtpEndpoint(endpoint_roots["ftps_implicit"], ImplicitTLSFTPHandler, certificate_path, tls_key_path, require_tls=True),
    ]
    sftp = SftpEndpoint(endpoint_roots["sftp"], root)
    endpoints.append(sftp)
    for endpoint in endpoints:
        endpoint.start()

    rotate_host_key_path = root / "rotate-sftp-host-key"
    host_key_generation = 1
    configuration = {
        "host": "127.0.0.1",
        "username": LAB_USER,
        "password": LAB_PASSWORD,
        "key_passphrase": LAB_KEY_PASSPHRASE,
        "seed_name": SEED_NAME,
        "seed_content": SEED_CONTENT,
        "tls_ca_path": str(certificate_path),
        "sftp_client_key_path": str(sftp.client_key_path),
        "sftp_encrypted_client_key_path": str(sftp.encrypted_client_key_path),
        "sftp_host_key_algorithm": sftp.host_key_algorithm,
        "sftp_host_key_base64": sftp.host_key_base64,
        "sftp_host_key_fingerprint": sftp.host_key_fingerprint,
        "sftp_host_key_generation": host_key_generation,
        "sftp_rotate_host_key_path": str(rotate_host_key_path),
        "endpoints": {
            "ftp": endpoints[0].port,
            "ftp_opportunistic_tls": endpoints[1].port,
            "ftps_explicit": endpoints[2].port,
            "ftps_implicit": endpoints[3].port,
            "sftp": sftp.port,
        },
    }
    def write_configuration() -> None:
        temporary = arguments.config.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(configuration, indent=2), encoding="utf-8")
        os.replace(temporary, arguments.config)

    arguments.config.parent.mkdir(parents=True, exist_ok=True)
    write_configuration()
    print(f"READY {arguments.config}", flush=True)
    try:
        if arguments.stop_file is None:
            sys.stdin.readline()
        else:
            while not arguments.stop_file.exists():
                if rotate_host_key_path.exists():
                    rotate_host_key_path.unlink()
                    sftp.rotate_host_key()
                    host_key_generation += 1
                    configuration["sftp_host_key_algorithm"] = sftp.host_key_algorithm
                    configuration["sftp_host_key_base64"] = sftp.host_key_base64
                    configuration["sftp_host_key_fingerprint"] = sftp.host_key_fingerprint
                    configuration["sftp_host_key_generation"] = host_key_generation
                    write_configuration()
                time.sleep(0.1)
    finally:
        for endpoint in reversed(endpoints):
            endpoint.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
