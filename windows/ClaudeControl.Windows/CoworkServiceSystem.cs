using System.ComponentModel;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ClaudeControl.Core;
using Microsoft.Win32.SafeHandles;

namespace ClaudeControl.Windows;

public sealed record CoworkPublisherVerification(bool SignatureValid, bool IsAnthropic,
    string Detail, string CertificateFingerprint = "");

// Only the exact, verified Claude packaged service is eligible for SCM STOP.
// This class never starts services, changes configuration, kills a PID, or downloads trust data.
public sealed class CoworkServiceSystem : ICoworkServiceSystem
{
    private const uint ManagerConnect = 1, QueryConfig = 1, QueryStatus = 4, StopAccess = 0x20;
    private const uint OwnProcess = 0x10, PackageService = 0x200, AcceptStop = 1;
    private const int AccessDenied = 5, NotInstalled = 1060, NotActive = 1062;

    public CoworkServiceResult Inspect()
    {
        if (!OperatingSystem.IsWindows()) return Failure(null, "Cowork 服务检查仅适用于 Windows。");
        try
        {
            using ServiceHandle manager = OpenSCManagerW(null, null, ManagerConnect);
            if (manager.IsInvalid) return ApiFailure(null, "打开本机服务管理器", Marshal.GetLastWin32Error());
            using ServiceHandle service = OpenServiceW(manager, CoworkServiceController.ServiceName, QueryConfig | QueryStatus);
            if (service.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                return error == NotInstalled ? new(null, []) : ApiFailure(null, "查询 Cowork 服务", error);
            }
            using ValidationLease lease = Validate(service);
            return lease.Result;
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { return Failure(null, "Cowork 服务检查失败：" + exception.Message, ElevationRequired(exception)); }
    }

    public CoworkServiceResult StopVerified(CoworkServiceSnapshot expected)
    {
        if (!OperatingSystem.IsWindows()) return Failure(expected, "Cowork 服务控制仅适用于 Windows。");
        if (!expected.Verified || !string.Equals(expected.Name, CoworkServiceController.ServiceName, StringComparison.OrdinalIgnoreCase))
            return Failure(expected, "Cowork 服务身份未验证，拒绝发送停止控制。");
        try
        {
            using ServiceHandle manager = OpenSCManagerW(null, null, ManagerConnect);
            if (manager.IsInvalid) return ApiFailure(expected, "打开本机服务管理器", Marshal.GetLastWin32Error());
            using ServiceHandle service = OpenServiceW(manager, CoworkServiceController.ServiceName, QueryConfig | QueryStatus | StopAccess);
            if (service.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                return error == NotInstalled ? new(null, []) : ApiFailure(expected, "取得 Cowork 服务停止权限", error);
            }
            // Keep the same SCM object plus read-only, non-writable file handles open
            // throughout verification and STOP. Never execute the configured PathName.
            using ValidationLease lease = Validate(service);
            CoworkServiceResult result = lease.Result;
            if (result.Warnings.Count > 0 || result.Snapshot is null || !result.Snapshot.Verified) return result;
            if (!string.Equals(expected.Identity, result.Snapshot.Identity, StringComparison.Ordinal))
                return Failure(result.Snapshot, "Cowork 服务配置、文件或签名身份已改变，未发送停止控制。");
            if (result.Snapshot.State is CoworkServiceState.Stopped or CoworkServiceState.StopPending) return result;
            ServiceConfiguration currentConfig = ReadConfiguration(service);
            ServiceStatus currentStatus = ReadStatus(service);
            if (currentConfig.Key != lease.ConfigurationKey || currentStatus.ProcessId != lease.ProcessId)
                return Failure(result.Snapshot, "Cowork 服务在验证后发生配置或进程变化，未发送停止控制。");
            if (currentStatus.State is 1 or 3) return result with { Snapshot = result.Snapshot with { State = (CoworkServiceState)currentStatus.State } };
            if ((currentStatus.ControlsAccepted & AcceptStop) == 0)
                return Failure(result.Snapshot, "Cowork 服务当前不接受停止控制；没有强杀进程或停止其他依赖服务。");
            if (!ControlService(service, 1, out _))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != NotActive) return ApiFailure(result.Snapshot, "发送 Cowork 服务 STOP", error);
            }
            ServiceStatus after = ReadStatus(service);
            return result with { Snapshot = result.Snapshot with { State = (CoworkServiceState)after.State } };
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { return Failure(expected, "Cowork 服务停止失败：" + exception.Message, ElevationRequired(exception)); }
    }

    public void Delay(TimeSpan duration) => Thread.Sleep(duration);

    // Read-only test entry point. This does not discover or control any service.
    public static CoworkPublisherVerification VerifyPublisherFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, "发布者验证仅适用于 Windows。");
        try
        {
            using SafeFileHandle file = PinFile(path);
            return VerifyPublisher(file, FinalPath(file));
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { return new(false, false, "签名验证失败：" + exception.Message); }
    }

    private static ValidationLease Validate(ServiceHandle service)
    {
        var lease = new ValidationLease();
        try
        {
            ServiceConfiguration config = ReadConfiguration(service);
            ServiceStatus status = ReadStatus(service);
            string executable = ParseExecutable(config.BinaryPath);
            var snapshot = new CoworkServiceSnapshot(CoworkServiceController.ServiceName,
                Enum.IsDefined(typeof(CoworkServiceState), (int)status.State) ? (CoworkServiceState)status.State : CoworkServiceState.Unknown,
                executable, false);
            lease.Result = new(snapshot, []);
            lease.ConfigurationKey = config.Key;
            lease.ProcessId = status.ProcessId;
            if (!IsSupportedServiceType(config.ServiceType) || !IsSupportedServiceType(status.ServiceType) ||
                !new[] { "LocalSystem", @"NT AUTHORITY\SYSTEM" }.Contains(config.Account, StringComparer.OrdinalIgnoreCase))
                return lease.Reject("服务不是预期的独立 LocalSystem Cowork 服务，未授权停止。");
            if (!Path.GetFileName(executable).Equals("cowork-svc.exe", StringComparison.OrdinalIgnoreCase))
                return lease.Reject("服务配置的可执行文件不是精确的 cowork-svc.exe。");
            lease.Binary = PinFile(executable);
            string canonical = FinalPath(lease.Binary);
            string package = ClaudePackageDirectory(canonical);
            string manifestPath = Path.Combine(package, "AppxManifest.xml");
            lease.Manifest = PinFile(manifestPath);
            if (!string.Equals(FinalPath(lease.Manifest), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
                return lease.Reject("Claude 包清单路径发生重定向，未授权停止。");
            byte[] manifestBytes = ReadPinned(lease.Manifest, 2 * 1024 * 1024);
            VerifyManifest(manifestBytes);
            CoworkPublisherVerification publisher = VerifyPublisher(lease.Binary, canonical);
            if (!publisher.SignatureValid || !publisher.IsAnthropic) return lease.Reject(publisher.Detail);
            // A running service's image must also agree with the validated configuration.
            if (status.ProcessId != 0 && status.State is 2 or 4 or 5 or 6 or 7)
            {
                lease.Process = OpenProcess(0x1000, false, checked((int)status.ProcessId));
                if (lease.Process.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    return lease.Reject("无法核实正在运行的 Cowork 服务映像：" + new Win32Exception(error).Message, error == AccessDenied);
                }
                var image = new StringBuilder(32768);
                uint length = (uint)image.Capacity;
                if (!QueryFullProcessImageNameW(lease.Process, 0, image, ref length))
                {
                    int error = Marshal.GetLastWin32Error();
                    return lease.Reject("无法核实正在运行的 Cowork 服务映像路径：" + new Win32Exception(error).Message, error == AccessDenied);
                }
                if (!string.Equals(NormalizePath(image.ToString()), canonical, StringComparison.OrdinalIgnoreCase))
                    return lease.Reject("正在运行的服务映像与已验证的 cowork-svc.exe 不一致，未授权停止。");
            }
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.Key + "\n" + canonical + "\n" +
                HashPinned(lease.Binary) + "\n" + Convert.ToHexString(SHA256.HashData(manifestBytes)) + "\n" + publisher.CertificateFingerprint)));
            lease.Result = new(snapshot with { Executable = canonical, Verified = true, Identity = identity,
                Verification = "SCM 配置、Claude 包清单、独立映像及离线 Authenticode 发布者均已验证。" }, []);
            return lease;
        }
        catch (Exception exception) when (ExpectedFailure(exception))
        {
            return lease.Reject("Cowork 服务身份验证失败：" + exception.Message, ElevationRequired(exception));
        }
    }

    public static bool IsSupportedServiceType(uint type) => (type & OwnProcess) != 0 && (type & ~(OwnProcess | PackageService)) == 0;

    private static string ParseExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains('%')) throw new InvalidDataException("服务路径为空或含无法安全解释的环境变量。");
        IReadOnlyList<string> arguments = WindowsProcessSystem.SplitArguments(command);
        if (arguments.Count == 0 || !Path.IsPathFullyQualified(arguments[0]) || arguments[0].StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("服务必须配置明确的本机绝对可执行路径。");
        return Path.GetFullPath(arguments[0]);
    }

    private static string ClaudePackageDirectory(string executable)
    {
        string windowsApps = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"));
        string relative = Path.GetRelativePath(windowsApps, executable);
        string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 4 || !Regex.IsMatch(parts[0], @"^Claude_[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_(x64|arm64|x86|neutral)__pzs8sxrjxfjjc$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !parts[1].Equals("app", StringComparison.OrdinalIgnoreCase) || !parts[2].Equals("resources", StringComparison.OrdinalIgnoreCase) ||
            !parts[3].Equals("cowork-svc.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("服务映像不在受支持的 Program Files\\WindowsApps\\Claude_*\\app\\resources 包路径。");
        return Path.Combine(windowsApps, parts[0]);
    }

    private static void VerifyManifest(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        XDocument document = XDocument.Load(reader);
        XElement? identity = document.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "Identity");
        if (identity?.Attribute("Name")?.Value != "Claude" || !OfficialPublisher(new X500DistinguishedName(identity.Attribute("Publisher")?.Value ?? "")))
            throw new InvalidDataException("包清单不是已确认的 Anthropic Claude 发布者身份。");
        bool found = document.Descendants().Where(e => e.Name.LocalName == "Extension" && e.Attribute("Category")?.Value == "windows.service" &&
            e.Attribute("Executable")?.Value.Replace('/', '\\').Equals(@"app\resources\cowork-svc.exe", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(e => e.Descendants()).Any(e => e.Name.LocalName == "Service" && e.Attribute("Name")?.Value == CoworkServiceController.ServiceName &&
                e.Attribute("StartAccount")?.Value.Equals("localSystem", StringComparison.OrdinalIgnoreCase) == true);
        if (!found) throw new InvalidDataException("Claude 包清单没有声明预期的 CoworkVMService 与 cowork-svc.exe 对应关系。");
    }

    private static SafeFileHandle PinFile(string path)
    {
        SafeFileHandle file = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new Win32Exception(error, "无法以只读方式锁定待验证文件。");
        }
        return file;
    }

    private static string FinalPath(SafeFileHandle file)
    {
        var path = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 0);
        if (count == 0 || count >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实文件最终路径。");
        return NormalizePath(path.ToString());
    }
    private static string NormalizePath(string path) => Path.GetFullPath(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);

    private static byte[] ReadPinned(SafeFileHandle file, int maximum)
    {
        long length = RandomAccess.GetLength(file);
        if (length < 1 || length > maximum) throw new InvalidDataException("待验证文件大小不在允许范围内。");
        byte[] bytes = new byte[(int)length];
        int total = 0;
        while (total < bytes.Length)
        {
            int read = RandomAccess.Read(file, bytes.AsSpan(total), total);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
        return bytes;
    }
    private static string HashPinned(SafeFileHandle file)
    {
        long length = RandomAccess.GetLength(file);
        if (length < 1 || length > 256 * 1024 * 1024) throw new InvalidDataException("Cowork 服务映像大小不在允许范围内。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long offset = 0;
        while (offset < length)
        {
            int read = RandomAccess.Read(file, buffer, offset);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static CoworkPublisherVerification VerifyPublisher(SafeFileHandle file, string path)
    {
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        IntPtr pathPointer = Marshal.StringToHGlobalUni(path);
        IntPtr infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = infoPointer, StateAction = 1, ProviderFlags = 0x1000 | 0x10 | 0x2000 };
        try
        {
            Marshal.StructureToPtr(new TrustFileInfo { Size = (uint)Marshal.SizeOf<TrustFileInfo>(), Path = pathPointer, File = file.DangerousGetHandle() }, infoPointer, false);
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) return new(false, false, $"离线 Authenticode 验证未通过（0x{unchecked((uint)result):X8}）；未联网补证书或授权停止。");
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            IntPtr cert = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (cert == IntPtr.Zero) return new(true, false, "签名有效，但无法取得已验证的实际发布者证书。");
            ProviderCertificate providerCert = Marshal.PtrToStructure<ProviderCertificate>(cert);
            CertificateContext context = Marshal.PtrToStructure<CertificateContext>(providerCert.Certificate);
            if (context.Encoded == IntPtr.Zero || context.Length == 0 || context.Length > 1024 * 1024) return new(true, false, "已验证发布者证书格式异常。");
            byte[] encoded = new byte[context.Length];
            Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(encoded);
            string fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            bool official = OfficialPublisher(certificate.SubjectName);
            return new(true, official, official ? "离线签名及 Anthropic, PBC 发布者验证通过。" : "签名有效，但发布者不是已确认的 Anthropic, PBC，未授权停止。", fingerprint);
        }
        finally
        {
            if (data.StateData != IntPtr.Zero) { data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            Marshal.FreeHGlobal(infoPointer);
            Marshal.FreeHGlobal(pathPointer);
            GC.KeepAlive(file);
        }
    }

    private static bool OfficialPublisher(X500DistinguishedName name)
    {
        var attributes = new Dictionary<string, string>();
        var reader = new AsnReader(name.RawData, AsnEncodingRules.DER).ReadSequence();
        while (reader.HasData)
        {
            var set = reader.ReadSetOf();
            while (set.HasData)
            {
                var entry = set.ReadSequence();
                string oid = entry.ReadObjectIdentifier();
                Asn1Tag tag = entry.PeekTag();
                if (tag.TagClass != TagClass.Universal) return false;
                string value = entry.ReadCharacterString((UniversalTagNumber)tag.TagValue);
                if (!attributes.TryAdd(oid, value) || entry.HasData) return false;
            }
        }
        return attributes.GetValueOrDefault("2.5.4.3") == "Anthropic, PBC" &&
            attributes.GetValueOrDefault("2.5.4.10") == "Anthropic, PBC" &&
            attributes.GetValueOrDefault("2.5.4.6") == "US" &&
            attributes.GetValueOrDefault("2.5.4.5") == "4860621";
    }

    private static ServiceConfiguration ReadConfiguration(ServiceHandle service)
    {
        QueryServiceConfigW(service, IntPtr.Zero, 0, out uint bytes);
        if (bytes == 0 || bytes > 1024 * 1024) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取服务配置大小。");
        IntPtr memory = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!QueryServiceConfigW(service, memory, bytes, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Cowork 服务配置。");
            NativeConfiguration config = Marshal.PtrToStructure<NativeConfiguration>(memory);
            return new(config.ServiceType, Marshal.PtrToStringUni(config.BinaryPath) ?? "", Marshal.PtrToStringUni(config.Account) ?? "");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static ServiceStatus ReadStatus(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out ServiceStatus status, (uint)Marshal.SizeOf<ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法复查 Cowork 服务状态。");
        return status;
    }
    private sealed record ServiceConfiguration(uint ServiceType, string BinaryPath, string Account)
    {
        public string Key => ServiceType + "\n" + BinaryPath + "\n" + Account;
    }
    private sealed class ValidationLease : IDisposable
    {
        public CoworkServiceResult Result { get; set; } = new(null, ["Cowork 服务身份尚未验证。"]);
        public string ConfigurationKey { get; set; } = "";
        public uint ProcessId { get; set; }
        public SafeFileHandle? Binary { get; set; }
        public SafeFileHandle? Manifest { get; set; }
        public SafeProcessHandle? Process { get; set; }
        public ValidationLease Reject(string reason, bool elevation = false) { Result = Failure(Result.Snapshot, reason, elevation); return this; }
        public void Dispose() { Process?.Dispose(); Manifest?.Dispose(); Binary?.Dispose(); }
    }
    private static bool ExpectedFailure(Exception exception) => exception is Win32Exception or IOException or UnauthorizedAccessException or CryptographicException or AsnContentException or XmlException or ArgumentException or InvalidOperationException or NotSupportedException;
    private static bool ElevationRequired(Exception exception) => exception is UnauthorizedAccessException || exception is Win32Exception win32 && win32.NativeErrorCode == AccessDenied;
    private static CoworkServiceResult Failure(CoworkServiceSnapshot? snapshot, string warning, bool elevation = false) =>
        new(snapshot is null ? null : snapshot with { Verified = false }, [warning], elevation);
    private static CoworkServiceResult ApiFailure(CoworkServiceSnapshot? snapshot, string action, int error) =>
        Failure(snapshot, action + "失败：" + new Win32Exception(error).Message, error == AccessDenied);

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeConfiguration
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint Tag;
        public IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public uint ServiceType, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicStatus
    {
        public uint ServiceType, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrustFileInfo { public uint Size; public IntPtr Path, File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size;
        public IntPtr Policy, Sip;
        public uint UiChoice, Revocation, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, Url;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public IntPtr Certificate; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { public uint Encoding; public IntPtr Encoded; public uint Length; public IntPtr Info, Store; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr configuration, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ServiceHandle service, uint control, out BasicStatus status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counter);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
}
