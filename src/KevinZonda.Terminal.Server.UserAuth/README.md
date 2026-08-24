# KTerm Server UserAuth

Creates and verifies the local Argon2id password hashes used by KTerm Server authentication.
The default configuration path is `%USERPROFILE%\.kterm\server_auth.json`.

```powershell
# Create a new file. Refuses to overwrite an existing file.
dotnet run --project src\KevinZonda.Terminal.Server.UserAuth -- init

# Add another allowed password for credential rotation.
dotnet run --project src\KevinZonda.Terminal.Server.UserAuth -- add

# Verify a password against the file.
dotnet run --project src\KevinZonda.Terminal.Server.UserAuth -- verify
```

Use `--file <path>` to override the default path. Passwords are read interactively and are never accepted
as command-line arguments. The generated file has this shape:

```json
{
  "allowedHash": [
    "$argon2id$v=19$m=65536,t=3,p=1$..."
  ]
}
```

This tool only manages password hashes. KTerm Server does not enforce the file until its HTTP/WebSocket
authentication pipeline is explicitly connected to this project. Authentication over a network must use HTTPS.
