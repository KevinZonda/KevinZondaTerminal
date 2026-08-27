# KTerm Server UserAuth Library

Provides the configuration store and Argon2id password operations used by KTerm Server authentication.
The default configuration path is `%USERPROFILE%\.kterm\server_auth.json`.

The command-line interface is hosted by `kterm-server`:

```powershell
# Create a new file. Refuses to overwrite an existing file.
kterm-server auth init

# Add another allowed password for credential rotation.
kterm-server auth add

# Verify a password against the file.
kterm-server auth verify
```

From the source tree, use `dotnet run --project src\KevinZonda.Terminal.Server -- auth <command>`.

Use `--file <path>` to override the default path. Passwords are read interactively and are never accepted
as command-line arguments. The generated file has this shape:

```json
{
  "allowedHash": [
    "$argon2id$v=19$m=65536,t=3,p=1$..."
  ]
}
```

KTerm Server loads this file in its default `auto` authentication mode. When the file exists and contains at
least one hash, the configured user can log in through the form at `/auth/login`; successful form authentication
is exchanged for the HttpOnly cookie required by the frontend and `/ws`. The user name defaults to `kterm` and
can be changed with the Server option `--custom-username`. A missing or empty file falls back to
no-password mode in `auto`, while `--auth-mode required` treats either condition as a startup error.
