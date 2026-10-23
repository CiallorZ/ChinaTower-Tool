using System.IO;
using System.Runtime.InteropServices;

namespace LinkForge.App.Services;

/// <summary>Authenticode verification state for the running executable.</summary>
public enum LfCodeSignatureState
{
    Unknown,
    Unsigned,
    Trusted,
    Invalid
}

/// <summary>One label/value pair shown on the About page.</summary>
public sealed record AboutFact(string Label, string Value);

/// <summary>Real facts about the running build and installation.</summary>
public sealed record AboutSnapshot(
    string Product,
    string Version,
    string InformationalVersion,
    string Commit,
    string Runtime,
    string OperatingSystem,
    string ProcessArchitecture,
    string RuntimeIdentifier,
    string ExecutablePath,
    string InstallationKind,
    LfCodeSignatureState SignatureState,
    string SignatureSummary,
    string DataDirectory,
    string LogsDirectory,
    string RepositoryUrl);

/// <summary>Reads version, installation and signature facts without inventing values.</summary>
public interface IAboutInfoService
{
    Task<AboutSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>System boundary for copying text and opening local folders or trusted project links.</summary>
public interface IAboutActionService
{
    void CopyText(string text);

    void OpenDirectory(string path);

    void OpenUrl(Uri uri);
}

/// <summary>Verifies the Authenticode signature of a local executable.</summary>
public interface ICodeSignatureVerifier
{
    LfCodeSignatureState Verify(string filePath);
}

/// <summary>Windows Authenticode verification through WinVerifyTrust.</summary>
public sealed class WindowsCodeSignatureVerifier : ICodeSignatureVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint TrustENoSig = 0x800B0100;
    private const uint TrustEExplicitDistrust = 0x800B0101;
    private const uint TrustESubjectFormUnknown = 0x800B010E;
    private const uint TrustESubjectNotTrusted = 0x800B010F;
    private const uint CertEUntrustedRoot = 0x800B0109;
    private const uint CertEChaining = 0x800B010A;
    private const uint CertEWrongUsage = 0x800B0110;
    private const uint TrustEBadDigest = 0x80096010;
    private const uint TrustETimeStamp = 0x80096005;

    /// <inheritdoc />
    public LfCodeSignatureState Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return LfCodeSignatureState.Unknown;
        }

        var fileInfoSize = Marshal.SizeOf<WinTrustFileInfo>();
        var fileInfoPointer = Marshal.AllocHGlobal(fileInfoSize);

        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)fileInfoSize,
                FilePath = filePath,
                FileHandle = IntPtr.Zero
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SIPClientData = IntPtr.Zero,
                UIChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = fileInfoPointer,
                StateAction = 0, // WTD_STATEACTION_VERIFY
                StateData = IntPtr.Zero,
                URLReference = IntPtr.Zero,
                ProvFlags = 0x00000010, // WTD_CACHE_ONLY_URL_RETRIEVAL
                UIContext = 0
            };

            var action = WinTrustActionGenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);

            trustData.StateAction = 1; // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);

            return result switch
            {
                0 => LfCodeSignatureState.Trusted,
                TrustENoSig or TrustESubjectFormUnknown => LfCodeSignatureState.Unsigned,
                TrustEExplicitDistrust or TrustESubjectNotTrusted or CertEUntrustedRoot
                    or CertEChaining or CertEWrongUsage or TrustEBadDigest or TrustETimeStamp
                    => LfCodeSignatureState.Invalid,
                _ => LfCodeSignatureState.Unknown
            };
        }
        catch (Exception)
        {
            return LfCodeSignatureState.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", PreserveSig = true)]
    private static extern uint WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProvFlags;
        public uint UIContext;
    }
}

/// <summary>Reads build facts on a worker thread and verifies the executable signature locally.</summary>
public sealed class WindowsAboutInfoService : IAboutInfoService
{
    private readonly AppEnvironment _environment;
    private readonly ICodeSignatureVerifier _signatureVerifier;

    public WindowsAboutInfoService(
        AppEnvironment environment,
        ICodeSignatureVerifier signatureVerifier)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(signatureVerifier);

        _environment = environment;
        _signatureVerifier = signatureVerifier;
    }

    /// <inheritdoc />
    public Task<AboutSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private AboutSnapshot Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var installationKind = DetectInstallationKind(executablePath);
        var signatureState = _signatureVerifier.Verify(executablePath);
        var dataDirectory = _environment.SettingsDirectory
            ?? throw new InvalidOperationException("无法确定本机数据目录。");
        var informationalVersion = _environment.ApplicationVersion;
        var version = ExtractProductVersion(informationalVersion);
        var commit = ExtractCommit(informationalVersion);

        cancellationToken.ThrowIfCancellationRequested();

        return new AboutSnapshot(
            Product: "LinkForge 视联网调试工具箱",
            Version: version,
            InformationalVersion: informationalVersion,
            Commit: commit,
            Runtime: _environment.RuntimeDescription,
            OperatingSystem: _environment.WindowsVersionName,
            ProcessArchitecture: _environment.ProcessArchitecture,
            RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            ExecutablePath: executablePath,
            InstallationKind: installationKind,
            SignatureState: signatureState,
            SignatureSummary: signatureState switch
            {
                LfCodeSignatureState.Trusted => "已签名，Windows 信任链验证通过",
                LfCodeSignatureState.Unsigned => "未签名（未提供代码签名证书）",
                LfCodeSignatureState.Invalid => "签名无效或证书链不受信任",
                _ => "签名状态未知（本地校验未返回确定结果）"
            },
            DataDirectory: dataDirectory,
            LogsDirectory: Path.Combine(dataDirectory, "logs"),
            RepositoryUrl: "https://github.com/CiallorZ/ChinaTower-Tool");
    }

    private static string DetectInstallationKind(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return "未知";
        }

        if (File.Exists(Path.Combine(directory, "unins000.exe")))
        {
            return "安装版（Inno Setup）";
        }

        return File.Exists(Path.Combine(directory, "release.json"))
            ? "便携包"
            : "开发构建";
    }

    private static string ExtractCommit(string informationalVersion)
    {
        var separator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return separator >= 0 && separator + 1 < informationalVersion.Length
            ? informationalVersion[(separator + 1)..]
            : "未记录";
    }

    private static string ExtractProductVersion(string informationalVersion)
    {
        var separator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return separator > 0 ? informationalVersion[..separator] : informationalVersion;
    }
}

/// <summary>Shell integration for clipboard and external launches.</summary>
public sealed class AboutActionService : IAboutActionService
{
    /// <inheritdoc />
    public void CopyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        System.Windows.Clipboard.SetText(text);
    }

    /// <inheritdoc />
    public void OpenDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("目录路径不能为空。", nameof(path));
        }

        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    /// <inheritdoc />
    public void OpenUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("只允许打开 HTTP(S) 链接。", nameof(uri));
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }
}
