using System.Diagnostics;
using System.IO;

namespace LinkForge.App.Tests.Packaging;

/// <summary>
/// Guards the release-only behavior that normal application tests cannot exercise:
/// file-version conversion and the installer's architecture/privacy boundary.
/// </summary>
public class ReleasePackagingTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("0.5.0-preview.1", "0.5.0.1")]
    [InlineData("2.3.4-rc12", "2.3.4.12")]
    public void Inno_file_version_should_remain_numeric(string productVersion, string expected)
    {
        var output = InvokeReleaseHelper(
            $"ConvertTo-InnoFileVersion -ProductVersion '{productVersion}'");

        output.Trim().ShouldBe(expected);
    }

    [Fact]
    public void Invalid_semantic_version_should_fail_conversion()
    {
        var result = InvokePowerShell(
            $". '{GetHelperPath()}'; ConvertTo-InnoFileVersion -ProductVersion '1.0'");

        result.ExitCode.ShouldNotBe(0);
        result.Error.ShouldContain("cannot be represented");
    }

    [Fact]
    public void Installer_should_embed_both_payloads_and_install_per_user()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "installer",
            "LinkForge.iss"));

        source.ShouldContain(@"Compression=lzma2/max");
        source.ShouldContain(@"DefaultDirName={localappdata}\Programs\{#MyAppShortName}");
        source.ShouldContain("PrivilegesRequired=lowest");
        source.ShouldContain(@"Source: ""{#PayloadRoot}\win-x64\*""");
        source.ShouldContain(@"Source: ""{#PayloadRoot}\win-x86\*""");
        source.ShouldContain("Check: IsX64Install");
        source.ShouldContain("Check: not IsX64Install");
        source.ShouldContain(@"MessagesFile: ""Languages\ChineseSimplified.isl""");
        source.ShouldContain("PrepareToInstall");
        source.ShouldContain("DeinitializeSetup");
        source.ShouldContain("upgrade-backup");
        source.ShouldContain("CopyDirectoryTree");
    }

    [Fact]
    public void Release_scripts_should_preserve_unsigned_and_test_boundaries()
    {
        var root = FindRepositoryRoot();
        var releaseScript = File.ReadAllText(Path.Combine(root, "scripts", "build-release.ps1"));
        var installerVerifier = File.ReadAllText(Path.Combine(root, "scripts", "verify-installer.ps1"));
        var upgradeFailureVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "verify-installer-upgrade-failure.ps1"));

        releaseScript.ShouldContain("Formal release requires a clean worktree");
        releaseScript.ShouldContain("Formal release requires an empty VersionSuffix");
        releaseScript.ShouldContain("codeSigning = 'unsigned'");
        releaseScript.ShouldContain("foreach ($runtime in @('win-x64', 'win-x86'))");
        releaseScript.ShouldContain("LinkForge-Setup-THIRD-PARTY-NOTICES.txt");

        installerVerifier.ShouldContain("UpgradeExitCode");
        installerVerifier.ShouldContain("FailedUpgradeExitCode");
        installerVerifier.ShouldContain("PreviousInstallationPreserved");
        installerVerifier.ShouldContain("UpgradeBackupRemoved");
        installerVerifier.ShouldContain("NOCLOSEAPPLICATIONS");
        installerVerifier.ShouldContain("removed the previous uninstaller");
        installerVerifier.ShouldContain("removed the previous Start Menu shortcut");
        installerVerifier.ShouldContain("UninstallExitCode");
        installerVerifier.ShouldContain("UserDataPreserved");
        installerVerifier.ShouldContain("Uninstall unexpectedly removed user data");

        upgradeFailureVerifier.ShouldContain("locks LinkForge.App.dll");
        upgradeFailureVerifier.ShouldContain("FailedUpgradeExitCode");
        upgradeFailureVerifier.ShouldContain("PreviousUninstallerPreserved");
        upgradeFailureVerifier.ShouldContain("PreviousStartMenuShortcutPreserved");
        upgradeFailureVerifier.ShouldContain("PreviousApplicationExitCode");
    }

    [Fact]
    public void Application_manifest_should_declare_supported_windows_and_dpi_modes()
    {
        var manifest = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LinkForge.App",
            "app.manifest"));

        manifest.ShouldContain(@"level=""asInvoker""");
        manifest.ShouldContain("PerMonitorV2, PerMonitor");
        manifest.ShouldContain("longPathAware");
        manifest.ShouldContain("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }

    [Fact]
    public void Vendored_installer_language_pack_should_include_its_license()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "installer", "Languages");

        File.Exists(Path.Combine(directory, "ChineseSimplified.isl")).ShouldBeTrue();
        var license = File.ReadAllText(Path.Combine(directory, "LICENSE-ChineseSimplified.txt"));
        license.ShouldContain("MIT License");
        license.ShouldContain("Permission is hereby granted");
    }

    private static string InvokeReleaseHelper(string expression)
    {
        var result = InvokePowerShell($". '{GetHelperPath()}'; {expression}");
        result.ExitCode.ShouldBe(0, result.Error);
        return result.Output;
    }

    private static (int ExitCode, string Output, string Error) InvokePowerShell(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start powershell.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static string GetHelperPath() =>
        Path.Combine(FindRepositoryRoot(), "scripts", "release-helpers.ps1");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LinkForge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Unable to find LinkForge.slnx above {AppContext.BaseDirectory}.");
    }
}
