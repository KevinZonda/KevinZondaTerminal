using System.Security.Cryptography.X509Certificates;
using KevinZonda.Terminal.Server.Launcher;
using KevinZonda.Terminal.Server.Login;
using KevinZonda.Terminal.Server.UserAuth;

var tests = new (string Name, Action Run)[]
{
    ("Generated certificate chain and PEM key", TestGeneratedCertificate),
    ("Generation refuses overwrite", TestOverwriteProtection),
    ("Launcher emits Kestrel PEM arguments", TestLauncherArguments),
    ("Launcher rejects incomplete or mismatched PEM files", TestInvalidCertificateConfiguration),
    ("Launcher validates custom usernames", TestCustomUsername),
    ("Launcher configures the login ICP registration", TestIcpRegistration),
    ("Credential Manager adds and deletes passwords", TestCredentialManager),
    ("Credential Manager generates strong random passwords", TestGeneratedPassword),
    ("Launcher resolves the effective authentication file", TestAuthFileResolver),
    ("Generator rejects unsafe domains", TestUnsafeDomain)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

return failures == 0 ? 0 : 1;

static void TestGeneratedCertificate()
{
    WithTemporaryDirectory(directory =>
    {
        var output = SelfSignedCertificateGenerator.Generate(
            "kterm-backend.example.test",
            rootDirectory: directory,
            subjectInformation: new CertificateSubjectInformation(
                CountryOrRegion: "cn",
                StateOrProvince: "Shanghai",
                Locality: "Shanghai",
                Organization: "KevinZonda Terminal",
                OrganizationalUnit: "Server",
                CertificateAuthorityCommonName: "KTerm Test Root CA"));
        Equal("kterm-backend.example.test", output.Domain);
        True(File.Exists(output.PublicCertificatePath));
        True(File.Exists(output.PrivateKeyPath));
        True(File.Exists(output.CertificateAuthorityPath));
        True(File.ReadAllText(output.PrivateKeyPath).Contains("BEGIN PRIVATE KEY"));
        False(File.ReadAllText(output.PrivateKeyPath).Contains("ENCRYPTED"));

        using var serverCertificate = X509Certificate2.CreateFromPemFile(
            output.PublicCertificatePath,
            output.PrivateKeyPath);
        using var authorityCertificate = X509Certificate2.CreateFromPem(
            File.ReadAllText(output.CertificateAuthorityPath));
        True(serverCertificate.HasPrivateKey);
        Equal(
            "kterm-backend.example.test",
            serverCertificate.GetNameInfo(X509NameType.DnsName, forIssuer: false));
        True(serverCertificate.Subject.Contains("C=CN", StringComparison.Ordinal));
        True(serverCertificate.Subject.Contains("O=KevinZonda Terminal", StringComparison.Ordinal));
        True(serverCertificate.Subject.Contains("OU=Server", StringComparison.Ordinal));
        Equal(
            "KTerm Test Root CA",
            authorityCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Equal(
            "KTerm Test Root CA",
            serverCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authorityCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        True(chain.Build(serverCertificate));
    });
}

static void TestOverwriteProtection()
{
    WithTemporaryDirectory(directory =>
    {
        SelfSignedCertificateGenerator.Generate("localhost", rootDirectory: directory);
        Throws<CertificateGenerationException>(() =>
            SelfSignedCertificateGenerator.Generate("localhost", rootDirectory: directory));
        var replaced = SelfSignedCertificateGenerator.Generate(
            "localhost",
            overwrite: true,
            rootDirectory: directory);
        True(File.Exists(replaced.CertificateAuthorityPath));
    });
}

static void TestLauncherArguments()
{
    WithTemporaryDirectory(directory =>
    {
        var output = SelfSignedCertificateGenerator.Generate(
            "kterm.example.test",
            rootDirectory: directory);
        var configuration = new LauncherConfiguration
        {
            Server = new LauncherServerConfiguration
            {
                Urls = "http://127.0.0.1:7132;https://127.0.0.1:7133",
                AuthMode = "required",
                CustomUsername = "operator",
                IcpRegistration = "沪ICP备12345678号-1",
                Certificate = new LauncherCertificateConfiguration
                {
                    PublicCertificatePath = output.PublicCertificatePath,
                    PrivateKeyPath = output.PrivateKeyPath
                }
            }
        };

        var arguments = configuration.BuildServerArguments([]);
        Equal(
            output.PublicCertificatePath,
            ValueAfter(arguments, "--Kestrel:Certificates:Default:Path"));
        Equal(
            output.PrivateKeyPath,
            ValueAfter(arguments, "--Kestrel:Certificates:Default:KeyPath"));
        Equal(
            "http://127.0.0.1:7132;https://127.0.0.1:7133",
            ValueAfter(arguments, "--urls"));
        Equal("operator", ValueAfter(arguments, "--custom-username"));
        Equal("沪ICP备12345678号-1", ValueAfter(arguments, "--icp-registration"));

        var path = Path.Combine(directory, "server_launcher.json");
        var store = new LauncherConfigurationStore(path);
        store.Save(configuration);
        var loaded = store.Load();
        Equal(output.PublicCertificatePath, loaded.Server.Certificate.PublicCertificatePath);
        Equal(output.PrivateKeyPath, loaded.Server.Certificate.PrivateKeyPath);
        Equal("operator", loaded.Server.CustomUsername);
        Equal("沪ICP备12345678号-1", loaded.Server.IcpRegistration);
    });
}

static void TestInvalidCertificateConfiguration()
{
    WithTemporaryDirectory(directory =>
    {
        var first = SelfSignedCertificateGenerator.Generate(
            "first.example.test",
            rootDirectory: directory);
        var second = SelfSignedCertificateGenerator.Generate(
            "second.example.test",
            rootDirectory: directory);

        Throws<LauncherConfigurationException>(() => new LauncherConfiguration
        {
            Server = new LauncherServerConfiguration
            {
                Certificate = new LauncherCertificateConfiguration
                {
                    PublicCertificatePath = first.PublicCertificatePath
                }
            }
        }.Normalize());

        Throws<LauncherConfigurationException>(() => new LauncherConfiguration
        {
            Server = new LauncherServerConfiguration
            {
                Certificate = new LauncherCertificateConfiguration
                {
                    PublicCertificatePath = first.PublicCertificatePath,
                    PrivateKeyPath = second.PrivateKeyPath
                }
            }
        }.Normalize());
    });
}

static void TestCustomUsername()
{
    Equal(
        "kterm",
        new LauncherConfiguration
        {
            Server = new LauncherServerConfiguration { CustomUsername = " " }
        }.Normalize().Server.CustomUsername);
    Equal(
        "operator",
        new LauncherConfiguration
        {
            Server = new LauncherServerConfiguration { CustomUsername = " operator " }
        }.Normalize().Server.CustomUsername);
    Throws<LauncherConfigurationException>(() => new LauncherConfiguration
    {
        Server = new LauncherServerConfiguration { CustomUsername = "bad:name" }
    }.Normalize());
    Throws<LauncherConfigurationException>(() => new LauncherConfiguration
    {
        Server = new LauncherServerConfiguration { CustomUsername = "bad\nname" }
    }.Normalize());
    Throws<LauncherConfigurationException>(() => new LauncherConfiguration
    {
        Server = new LauncherServerConfiguration { CustomUsername = new string('a', 129) }
    }.Normalize());
}

static void TestIcpRegistration()
{
    var normalized = new LauncherConfiguration
    {
        Server = new LauncherServerConfiguration
        {
            IcpRegistration = " 沪ICP备12345678号-1 "
        }
    }.Normalize();
    Equal("沪ICP备12345678号-1", normalized.Server.IcpRegistration);
    Equal(
        "沪ICP备12345678号-1",
        ValueAfter(normalized.BuildServerArguments([]), "--icp-registration"));

    var empty = new LauncherConfiguration
    {
        Server = new LauncherServerConfiguration { IcpRegistration = " " }
    }.Normalize();
    Equal<string?>(null, empty.Server.IcpRegistration);
    False(empty.BuildServerArguments([]).Contains("--icp-registration", StringComparer.Ordinal));

    Throws<LauncherConfigurationException>(() => new LauncherConfiguration
    {
        Server = new LauncherServerConfiguration { IcpRegistration = "沪ICP备\n12345678号" }
    }.Normalize());
    Throws<LauncherConfigurationException>(() => new LauncherConfiguration
    {
        Server = new LauncherServerConfiguration { IcpRegistration = new string('a', 129) }
    }.Normalize());

    var rendered = EmbeddedLoginPage.Render(
        "csrf",
        "/",
        icpRegistration: "沪ICP备12345678号-1<script>alert(1)</script>");
    True(rendered.Contains("https://beian.miit.gov.cn/", StringComparison.Ordinal));
    True(rendered.Contains("target=\"_blank\"", StringComparison.Ordinal));
    True(rendered.Contains("rel=\"noopener noreferrer\"", StringComparison.Ordinal));
    True(rendered.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal));
    False(rendered.Contains("<script>alert(1)</script>", StringComparison.Ordinal));

    var withoutRegistration = EmbeddedLoginPage.Render("csrf", "/");
    False(withoutRegistration.Contains("beian.miit.gov.cn", StringComparison.Ordinal));
    False(withoutRegistration.Contains("class=\"registration\"", StringComparison.Ordinal));
}

static void TestCredentialManager()
{
    WithTemporaryDirectory(directory =>
    {
        var path = Path.Combine(directory, "server_auth.json");
        var manager = new CredentialManager(path);
        Equal(0, manager.LoadAsync().GetAwaiter().GetResult().AllowedHash.Length);

        var added = manager.AddPasswordAsync("correct horse battery staple")
            .GetAwaiter().GetResult();
        Equal(1, added.AllowedHash.Length);
        True(new Argon2PasswordService().Verify(
            "correct horse battery staple",
            added.AllowedHash[0]));
        var entries = CredentialManager.GetEntries(added);
        Equal(1, entries.Count);
        Equal(16, entries[0].Fingerprint.Length);
        Throws<CredentialManagementException>(() => manager
            .AddPasswordAsync("correct horse battery staple")
            .GetAwaiter().GetResult());

        var deleted = manager.DeleteAsync(added.AllowedHash[0]).GetAwaiter().GetResult();
        Equal(0, deleted.AllowedHash.Length);
        Equal(0, manager.LoadAsync().GetAwaiter().GetResult().AllowedHash.Length);
    });
}

static void TestGeneratedPassword()
{
    var first = CredentialManager.GenerateRandomPassword();
    var second = CredentialManager.GenerateRandomPassword();
    Equal(32, first.Length);
    True(first.Any(char.IsUpper));
    True(first.Any(char.IsLower));
    True(first.Any(char.IsDigit));
    True(first.Any(character => !char.IsLetterOrDigit(character)));
    False(string.Equals(first, second, StringComparison.Ordinal));
    Throws<ArgumentOutOfRangeException>(() => CredentialManager.GenerateRandomPassword(15));
}

static void TestAuthFileResolver()
{
    WithTemporaryDirectory(directory =>
    {
        var first = Path.Combine(directory, "first.json");
        var second = Path.Combine(directory, "second.json");
        Equal(
            Path.GetFullPath(second),
            ServerAuthFileArgumentResolver.Resolve(
                ["--auth-file", first, $"--auth-file={second}"]));
        Equal(
            Path.Combine(directory, "relative.json"),
            ServerAuthFileArgumentResolver.Resolve(
                ["--auth-file", "relative.json"],
                directory));
        Throws<CredentialManagementException>(() =>
            ServerAuthFileArgumentResolver.Resolve(["--auth-file"]));
        Throws<CredentialManagementException>(() =>
            ServerAuthFileArgumentResolver.Resolve(["--auth-file="]));
    });
}

static void TestUnsafeDomain()
{
    Throws<CertificateGenerationException>(() =>
        SelfSignedCertificateGenerator.GetOutputPaths("../outside"));
    Throws<CertificateGenerationException>(() =>
        SelfSignedCertificateGenerator.GetOutputPaths("not a domain"));
    Throws<CertificateGenerationException>(() =>
        SelfSignedCertificateGenerator.ValidateSubjectInformation(
            new CertificateSubjectInformation(CountryOrRegion: "China")));
    Throws<CertificateGenerationException>(() =>
        SelfSignedCertificateGenerator.ValidateSubjectInformation(
            new CertificateSubjectInformation(CertificateAuthorityCommonName: "")));
}

static string ValueAfter(IReadOnlyList<string> arguments, string name)
{
    for (var index = 0; index + 1 < arguments.Count; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }
    throw new InvalidOperationException($"Argument not found: {name}");
}

static void WithTemporaryDirectory(Action<string> action)
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"kterm-launcher-certificate-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        action(directory);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void True(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Expected true.");
    }
}

static void False(bool condition) => True(!condition);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
