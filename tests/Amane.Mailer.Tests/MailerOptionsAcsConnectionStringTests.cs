using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerOptionsAcsConnectionStringTests
{
    [Fact]
    public void Prefers_the_file_based_secret_when_present()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amane-mailer-options-acs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "acs_connection_string");
        File.WriteAllText(filePath, "Endpoint=https://from-file;AccessKey=file-value\n");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ACS_CONNECTION_STRING_FILE"] = filePath,
                    ["ACS_CONNECTION_STRING"] = "Endpoint=https://from-env;AccessKey=env-value",
                })
                .Build();

            var options = MailerOptions.Load(configuration);

            Assert.Equal("Endpoint=https://from-file;AccessKey=file-value", options.AcsConnectionString);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Falls_back_to_the_env_var_when_no_file_is_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACS_CONNECTION_STRING"] = "Endpoint=https://from-env;AccessKey=env-value",
            })
            .Build();

        var options = MailerOptions.Load(configuration);

        Assert.Equal("Endpoint=https://from-env;AccessKey=env-value", options.AcsConnectionString);
    }

    [Fact]
    public void Falls_back_to_the_env_var_when_the_configured_file_does_not_exist()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACS_CONNECTION_STRING_FILE"] = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")),
                ["ACS_CONNECTION_STRING"] = "Endpoint=https://from-env;AccessKey=env-value",
            })
            .Build();

        var options = MailerOptions.Load(configuration);

        Assert.Equal("Endpoint=https://from-env;AccessKey=env-value", options.AcsConnectionString);
    }

    [Fact]
    public void Falls_back_to_the_env_var_when_the_configured_file_is_empty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amane-mailer-options-acs-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "acs_connection_string");
        File.WriteAllText(filePath, "   \n");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ACS_CONNECTION_STRING_FILE"] = filePath,
                    ["ACS_CONNECTION_STRING"] = "Endpoint=https://from-env;AccessKey=env-value",
                })
                .Build();

            var options = MailerOptions.Load(configuration);

            Assert.Equal("Endpoint=https://from-env;AccessKey=env-value", options.AcsConnectionString);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Is_empty_when_neither_the_file_nor_the_env_var_is_configured()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = MailerOptions.Load(configuration);

        Assert.Equal(string.Empty, options.AcsConnectionString);
    }

    /// <summary>
    /// MAILER_REQUIRE_ACS_SECRET_FILE=true is what infra/deploy/compose.yml sets on the `mailer`
    /// service only (see DeployComposeAcsBoundaryTests). This is the Staging/Production-equivalent
    /// case: even though a bare ACS_CONNECTION_STRING is present in the process environment, it
    /// must never be used — the missing/empty file fails closed to an empty connection string,
    /// which AcsMailDeliveryProvider already turns into a hard ACS_NOT_CONFIGURED failure.
    /// </summary>
    [Fact]
    public void Fails_closed_and_ignores_the_env_var_when_the_secret_file_is_required_but_missing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_REQUIRE_ACS_SECRET_FILE"] = "true",
                ["ACS_CONNECTION_STRING"] = "Endpoint=https://from-env;AccessKey=env-value",
            })
            .Build();

        var options = MailerOptions.Load(configuration);

        Assert.Equal(string.Empty, options.AcsConnectionString);
    }

    [Fact]
    public void Fails_closed_and_ignores_the_env_var_when_the_secret_file_is_required_but_empty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amane-mailer-options-acs-required-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "acs_connection_string");
        File.WriteAllText(filePath, string.Empty);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MAILER_REQUIRE_ACS_SECRET_FILE"] = "true",
                    ["ACS_CONNECTION_STRING_FILE"] = filePath,
                    ["ACS_CONNECTION_STRING"] = "Endpoint=https://from-env;AccessKey=env-value",
                })
                .Build();

            var options = MailerOptions.Load(configuration);

            Assert.Equal(string.Empty, options.AcsConnectionString);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The file still wins over the flag when both are present and valid — this is the normal
    /// successful Staging/Production path after `admin provider register-acs` has run.
    /// </summary>
    [Fact]
    public void Prefers_the_file_even_when_the_secret_file_is_required()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amane-mailer-options-acs-required-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "acs_connection_string");
        File.WriteAllText(filePath, "Endpoint=https://from-file;AccessKey=file-value");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MAILER_REQUIRE_ACS_SECRET_FILE"] = "true",
                    ["ACS_CONNECTION_STRING_FILE"] = filePath,
                })
                .Build();

            var options = MailerOptions.Load(configuration);

            Assert.Equal("Endpoint=https://from-file;AccessKey=file-value", options.AcsConnectionString);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Matches the local ACS drill exactly: MAILER_REQUIRE_ACS_SECRET_FILE is never set by
    /// infra/deploy/drills/mail-05a-acs-drill.sh's compose override, so its bare
    /// ACS_CONNECTION_STRING fallback must keep working unchanged.
    /// </summary>
    [Fact]
    public void Local_drill_style_configuration_without_the_flag_still_falls_back_to_the_env_var()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACS_CONNECTION_STRING"] = "Endpoint=https://from-env;AccessKey=env-value",
            })
            .Build();

        var options = MailerOptions.Load(configuration);

        Assert.Equal("Endpoint=https://from-env;AccessKey=env-value", options.AcsConnectionString);
    }
}
