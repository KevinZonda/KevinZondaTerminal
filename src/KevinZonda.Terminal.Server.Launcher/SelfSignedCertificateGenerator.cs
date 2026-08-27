using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KevinZonda.Terminal.Server.Launcher;

internal sealed record GeneratedCertificateFiles(
    string Domain,
    string PublicCertificatePath,
    string PrivateKeyPath,
    string CertificateAuthorityPath);

internal sealed record CertificateSubjectInformation(
    string? CountryOrRegion = null,
    string? StateOrProvince = null,
    string? Locality = null,
    string? Organization = null,
    string? OrganizationalUnit = null,
    string CertificateAuthorityCommonName = "KTerm Local Certificate Authority");

internal static class SelfSignedCertificateGenerator
{
    private static readonly UTF8Encoding PemEncoding = new(encoderShouldEmitUTF8Identifier: false);

    internal static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".kterm",
        "cert");

    internal static GeneratedCertificateFiles GetOutputPaths(
        string domain,
        string? rootDirectory = null)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var root = Path.GetFullPath(rootDirectory ?? DefaultRootDirectory);
        var directory = Path.GetFullPath(Path.Combine(root, normalizedDomain));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new CertificateGenerationException("The certificate directory is invalid.");
        }

        return new GeneratedCertificateFiles(
            normalizedDomain,
            Path.Combine(directory, "pub.pem"),
            Path.Combine(directory, "priv.pem"),
            Path.Combine(directory, "ca.pem"));
    }

    internal static GeneratedCertificateFiles Generate(
        string domain,
        bool overwrite = false,
        string? rootDirectory = null,
        CertificateSubjectInformation? subjectInformation = null)
    {
        var output = GetOutputPaths(domain, rootDirectory);
        var subject = NormalizeSubjectInformation(subjectInformation);
        var targetPaths = new[]
        {
            output.PublicCertificatePath,
            output.PrivateKeyPath,
            output.CertificateAuthorityPath
        };
        if (!overwrite && targetPaths.Any(File.Exists))
        {
            throw new CertificateGenerationException(
                $"Certificate files already exist for {output.Domain}.");
        }

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var authorityKey = RSA.Create(3072);
        var authorityRequest = new CertificateRequest(
            BuildDistinguishedName(subject.CertificateAuthorityCommonName, subject),
            authorityKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: true,
            pathLengthConstraint: 0,
            critical: true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            authorityRequest.PublicKey,
            critical: false));
        using var authorityCertificate = authorityRequest.CreateSelfSigned(
            notBefore,
            DateTimeOffset.UtcNow.AddYears(10));

        using var serverKey = RSA.Create(3072);
        var serverRequest = new CertificateRequest(
            BuildDistinguishedName(output.Domain, subject),
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1", "TLS Web Server Authentication")
            },
            critical: false));
        serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            serverRequest.PublicKey,
            critical: false));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(output.Domain);
        if (!string.Equals(output.Domain, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            subjectAlternativeNames.AddDnsName("localhost");
        }
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        serverRequest.CertificateExtensions.Add(subjectAlternativeNames.Build(critical: false));

        var serialNumber = RandomNumberGenerator.GetBytes(16);
        serialNumber[0] &= 0x7f;
        serialNumber[0] |= 0x01;
        using var issuedCertificate = serverRequest.Create(
            authorityCertificate,
            notBefore,
            DateTimeOffset.UtcNow.AddDays(825),
            serialNumber);
        using var serverCertificate = issuedCertificate.CopyWithPrivateKey(serverKey);

        var directory = Path.GetDirectoryName(output.PublicCertificatePath)
            ?? throw new CertificateGenerationException("The certificate directory is invalid.");
        Directory.CreateDirectory(directory);
        var temporaryPaths = targetPaths.Select(path =>
            Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp")).ToArray();
        try
        {
            File.WriteAllText(
                temporaryPaths[0],
                EnsureTrailingNewLine(serverCertificate.ExportCertificatePem()),
                PemEncoding);
            File.WriteAllText(
                temporaryPaths[1],
                EnsureTrailingNewLine(serverKey.ExportPkcs8PrivateKeyPem()),
                PemEncoding);
            File.WriteAllText(
                temporaryPaths[2],
                EnsureTrailingNewLine(authorityCertificate.ExportCertificatePem()),
                PemEncoding);

            for (var index = 0; index < targetPaths.Length; index++)
            {
                File.Move(temporaryPaths[index], targetPaths[index], overwrite);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new CertificateGenerationException(
                $"Unable to generate certificate files for {output.Domain}.",
                exception);
        }
        finally
        {
            foreach (var temporaryPath in temporaryPaths)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return output;
    }

    internal static void ValidateSubjectInformation(CertificateSubjectInformation? value) =>
        _ = NormalizeSubjectInformation(value);

    private static X500DistinguishedName BuildDistinguishedName(
        string commonName,
        CertificateSubjectInformation subject)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        if (subject.OrganizationalUnit is not null)
        {
            builder.AddOrganizationalUnitName(subject.OrganizationalUnit);
        }
        if (subject.Organization is not null)
        {
            builder.AddOrganizationName(subject.Organization);
        }
        if (subject.Locality is not null)
        {
            builder.AddLocalityName(subject.Locality);
        }
        if (subject.StateOrProvince is not null)
        {
            builder.AddStateOrProvinceName(subject.StateOrProvince);
        }
        if (subject.CountryOrRegion is not null)
        {
            builder.AddCountryOrRegion(subject.CountryOrRegion);
        }
        return builder.Build();
    }

    private static CertificateSubjectInformation NormalizeSubjectInformation(
        CertificateSubjectInformation? value)
    {
        value ??= new CertificateSubjectInformation();
        var country = NormalizeOptionalSubjectValue(
            value.CountryOrRegion,
            "Country/region",
            maximumLength: 2);
        if (country is not null &&
            (country.Length != 2 || country.Any(character => !char.IsAsciiLetter(character))))
        {
            throw new CertificateGenerationException(
                "Country/region must be a two-letter ISO country code such as CN or US.");
        }

        return new CertificateSubjectInformation(
            country?.ToUpperInvariant(),
            NormalizeOptionalSubjectValue(value.StateOrProvince, "State/province"),
            NormalizeOptionalSubjectValue(value.Locality, "Locality"),
            NormalizeOptionalSubjectValue(value.Organization, "Organization"),
            NormalizeOptionalSubjectValue(value.OrganizationalUnit, "Organizational unit"),
            NormalizeRequiredSubjectValue(
                value.CertificateAuthorityCommonName,
                "CA common name"));
    }

    private static string NormalizeRequiredSubjectValue(string? value, string fieldName) =>
        NormalizeOptionalSubjectValue(value, fieldName)
        ?? throw new CertificateGenerationException($"{fieldName} is required.");

    private static string? NormalizeOptionalSubjectValue(
        string? value,
        string fieldName,
        int maximumLength = 128)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }
        if (normalized.Length > maximumLength)
        {
            throw new CertificateGenerationException(
                $"{fieldName} must be {maximumLength} characters or fewer.");
        }
        if (normalized.Any(char.IsControl))
        {
            throw new CertificateGenerationException($"{fieldName} contains invalid characters.");
        }
        return normalized;
    }

    private static string NormalizeDomain(string value)
    {
        var domain = value?.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new CertificateGenerationException("A certificate domain is required.");
        }

        try
        {
            domain = new IdnMapping().GetAscii(domain).ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new CertificateGenerationException("The certificate domain is invalid.", exception);
        }
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns)
        {
            throw new CertificateGenerationException(
                "The certificate domain must be a DNS name such as localhost or kterm.example.com.");
        }
        return domain;
    }

    private static string EnsureTrailingNewLine(string value) =>
        value.EndsWith('\n') ? value : value + Environment.NewLine;
}

internal sealed class CertificateGenerationException : Exception
{
    internal CertificateGenerationException(string message)
        : base(message)
    {
    }

    internal CertificateGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
