using System.Text.Json;
using KevinZonda.Terminal.Server.UserAuth;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Argon2id hash round trip", TestHashRoundTrip),
    ("Argon2id uses random salts", TestRandomSalts),
    ("Any allowed hash can authenticate", TestVerifyAny),
    ("Configuration round trip", TestConfigurationRoundTrip),
    ("Create refuses to overwrite", TestCreateRefusesOverwrite),
    ("Empty configuration can be inspected by auto mode", TestLoadAllowingEmpty),
    ("Malformed configurations fail closed", TestMalformedConfigurations),
    ("Unsafe PHC parameters are rejected", TestUnsafeParameters),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

return failures == 0 ? 0 : 1;

static Task TestHashRoundTrip()
{
    var passwords = new Argon2PasswordService();
    var hash = passwords.Hash("correct horse battery staple");
    True(hash.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", StringComparison.Ordinal));
    True(passwords.Verify("correct horse battery staple", hash));
    False(passwords.Verify("wrong password", hash));
    passwords.ValidateEncodedHash(hash);
    return Task.CompletedTask;
}

static Task TestRandomSalts()
{
    var passwords = new Argon2PasswordService();
    var first = passwords.Hash("same password");
    var second = passwords.Hash("same password");
    False(string.Equals(first, second, StringComparison.Ordinal));
    True(passwords.Verify("same password", first));
    True(passwords.Verify("same password", second));
    return Task.CompletedTask;
}

static Task TestVerifyAny()
{
    var passwords = new Argon2PasswordService();
    var hashes = new[]
    {
        passwords.Hash("first password"),
        passwords.Hash("second password")
    };
    True(passwords.VerifyAny("second password", hashes));
    False(passwords.VerifyAny("third password", hashes));
    return Task.CompletedTask;
}

static async Task TestConfigurationRoundTrip()
{
    await WithTemporaryDirectory(async directory =>
    {
        var passwords = new Argon2PasswordService();
        var path = Path.Combine(directory, "server_auth.json");
        var store = new ServerAuthStore(path, passwords);
        var firstHash = passwords.Hash("first password");
        await store.CreateAsync(new ServerAuthConfiguration { AllowedHash = [firstHash] });

        var loaded = await store.LoadAsync();
        Equal(1, loaded.AllowedHash.Length);
        Equal(firstHash, loaded.AllowedHash[0]);

        var secondHash = passwords.Hash("second password");
        await store.SaveAsync(loaded with { AllowedHash = [firstHash, secondHash] });
        loaded = await store.LoadAsync();
        Equal(2, loaded.AllowedHash.Length);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        True(document.RootElement.TryGetProperty("allowedHash", out var hashes));
        Equal(2, hashes.GetArrayLength());
    });
}

static async Task TestCreateRefusesOverwrite()
{
    await WithTemporaryDirectory(async directory =>
    {
        var passwords = new Argon2PasswordService();
        var path = Path.Combine(directory, "server_auth.json");
        var store = new ServerAuthStore(path, passwords);
        var configuration = new ServerAuthConfiguration
        {
            AllowedHash = [passwords.Hash("password")]
        };
        await store.CreateAsync(configuration);
        await ThrowsAsync<AuthConfigurationException>(() => store.CreateAsync(configuration));
        var loaded = await store.LoadAsync();
        Equal(configuration.AllowedHash[0], loaded.AllowedHash[0]);
    });
}

static async Task TestLoadAllowingEmpty()
{
    await WithTemporaryDirectory(async directory =>
    {
        var path = Path.Combine(directory, "server_auth.json");
        var store = new ServerAuthStore(path);
        await File.WriteAllTextAsync(path, """{"allowedHash":[]}""");

        var configuration = await store.LoadAllowingEmptyAsync();
        Equal(0, configuration.AllowedHash.Length);
        await ThrowsAsync<AuthConfigurationException>(() => store.LoadAsync());
    });
}

static async Task TestMalformedConfigurations()
{
    await WithTemporaryDirectory(async directory =>
    {
        var path = Path.Combine(directory, "server_auth.json");
        var store = new ServerAuthStore(path);

        await File.WriteAllTextAsync(path, "{ invalid json");
        await ThrowsAsync<AuthConfigurationException>(() => store.LoadAsync());

        await File.WriteAllTextAsync(path, """{"allowedHash":[]}""");
        await ThrowsAsync<AuthConfigurationException>(() => store.LoadAsync());

        await File.WriteAllTextAsync(path, """{"allowedHash":null}""");
        await ThrowsAsync<AuthConfigurationException>(() => store.LoadAsync());

        await File.WriteAllTextAsync(path, """{"allowedHash":["not-a-hash"]}""");
        await ThrowsAsync<AuthConfigurationException>(() => store.LoadAsync());

        await File.WriteAllTextAsync(path, """{"allowedHash":["not-a-hash"],"unexpected":true}""");
        await ThrowsAsync<AuthConfigurationException>(() => store.LoadAsync());
    });
}

static Task TestUnsafeParameters()
{
    var passwords = new Argon2PasswordService();
    var hash = passwords.Hash("password");
    var excessiveMemory = hash.Replace("m=65536", "m=999999999", StringComparison.Ordinal);
    False(passwords.TryValidateEncodedHash(excessiveMemory, out _));
    False(passwords.Verify("password", excessiveMemory));

    var wrongVariant = hash.Replace("$argon2id$", "$argon2i$", StringComparison.Ordinal);
    False(passwords.TryValidateEncodedHash(wrongVariant, out _));
    return Task.CompletedTask;
}

static async Task WithTemporaryDirectory(Func<string, Task> action)
{
    var directory = Path.Combine(Path.GetTempPath(), $"kterm-user-auth-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        await action(directory);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
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
