using KevinZonda.Terminal.Server.UserAuth;

return await UserAuthCommand.RunAsync(args);

internal static class UserAuthCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (options.ShowHelp)
            {
                WriteUsage();
                return 0;
            }

            var passwords = new Argon2PasswordService();
            var store = new ServerAuthStore(options.File, passwords);
            return options.Command switch
            {
                "init" => await InitializeAsync(store, passwords).ConfigureAwait(false),
                "add" => await AddAsync(store, passwords).ConfigureAwait(false),
                "verify" => await VerifyAsync(store, passwords).ConfigureAwait(false),
                _ => throw new CommandLineException("Specify one of: init, add, verify.")
            };
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            WriteUsage(Console.Error);
            return 2;
        }
        catch (AuthConfigurationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
    }

    private static async Task<int> InitializeAsync(
        ServerAuthStore store,
        Argon2PasswordService passwords)
    {
        var password = ReadNewPassword();
        var configuration = new ServerAuthConfiguration
        {
            AllowedHash = [passwords.Hash(password)]
        };
        await store.CreateAsync(configuration).ConfigureAwait(false);
        Console.WriteLine($"Created server authentication configuration: {store.ConfigurationPath}");
        return 0;
    }

    private static async Task<int> AddAsync(
        ServerAuthStore store,
        Argon2PasswordService passwords)
    {
        var configuration = await store.LoadAsync().ConfigureAwait(false);
        if (configuration.AllowedHash.Length >= ServerAuthConfiguration.MaximumAllowedHashes)
        {
            throw new AuthConfigurationException(
                $"The configuration already contains the maximum of {ServerAuthConfiguration.MaximumAllowedHashes} hashes.");
        }

        var password = ReadNewPassword();
        if (passwords.VerifyAny(password, configuration.AllowedHash))
        {
            Console.WriteLine("That password is already allowed; the configuration was not changed.");
            return 0;
        }

        await store.SaveAsync(configuration with
        {
            AllowedHash = [.. configuration.AllowedHash, passwords.Hash(password)]
        }).ConfigureAwait(false);
        Console.WriteLine($"Added an allowed password to: {store.ConfigurationPath}");
        return 0;
    }

    private static async Task<int> VerifyAsync(
        ServerAuthStore store,
        Argon2PasswordService passwords)
    {
        var configuration = await store.LoadAsync().ConfigureAwait(false);
        var password = ReadPassword("Password: ");
        if (passwords.VerifyAny(password, configuration.AllowedHash))
        {
            Console.WriteLine("Password accepted.");
            return 0;
        }

        Console.Error.WriteLine("Password rejected.");
        return 1;
    }

    private static string ReadNewPassword()
    {
        var password = ReadPassword("Password: ");
        if (password.Length == 0)
        {
            throw new CommandLineException("The password cannot be empty.");
        }

        var confirmation = ReadPassword("Confirm password: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new CommandLineException("The passwords do not match.");
        }
        return password;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? throw new OperationCanceledException();
        }

        var password = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string([.. password]);
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Count > 0)
                {
                    password.RemoveAt(password.Count - 1);
                }
                continue;
            }
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Console.WriteLine();
                throw new OperationCanceledException();
            }
            if (!char.IsControl(key.KeyChar))
            {
                password.Add(key.KeyChar);
            }
        }
    }

    private static CommandOptions Parse(string[] args)
    {
        string? command = null;
        string? file = null;
        var showHelp = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "-h" or "--help":
                    showHelp = true;
                    break;

                case "--file":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new CommandLineException("--file requires a path.");
                    }
                    file = args[index];
                    break;

                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CommandLineException($"Unknown option: {argument}");
                    }
                    if (command is not null)
                    {
                        throw new CommandLineException($"Unexpected argument: {argument}");
                    }
                    command = argument.ToLowerInvariant();
                    break;
            }
        }

        return new CommandOptions(command, file, showHelp);
    }

    private static void WriteUsage(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("Usage: kterm-server-auth <init|add|verify> [--file <path>]");
        writer.WriteLine();
        writer.WriteLine($"Default file: {ServerAuthStore.DefaultConfigurationPath}");
    }

    private sealed record CommandOptions(string? Command, string? File, bool ShowHelp);

    private sealed class CommandLineException(string message) : Exception(message);
}
