using System.Runtime.InteropServices;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.NonInteractive;

namespace Amane.Mailer.Tests.Setup.NonInteractive;

public sealed class SetupNonInteractiveConfigReaderTests
{
    [Fact]
    public void Linux_statx_buffer_matches_uapi_size_and_key_offsets()
    {
        Assert.Equal(
            SetupNonInteractiveConfigReader.LinuxStatxSize,
            Marshal.SizeOf<SetupNonInteractiveConfigReader.LinuxStatxBuffer>());

        Assert.Equal(0, Marshal.OffsetOf<SetupNonInteractiveConfigReader.LinuxStatxBuffer>(nameof(SetupNonInteractiveConfigReader.LinuxStatxBuffer.Mask)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<SetupNonInteractiveConfigReader.LinuxStatxBuffer>(nameof(SetupNonInteractiveConfigReader.LinuxStatxBuffer.Uid)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<SetupNonInteractiveConfigReader.LinuxStatxBuffer>(nameof(SetupNonInteractiveConfigReader.LinuxStatxBuffer.Mode)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<SetupNonInteractiveConfigReader.LinuxStatxBuffer>(nameof(SetupNonInteractiveConfigReader.LinuxStatxBuffer.Size)).ToInt32());
        Assert.Equal(136, Marshal.OffsetOf<SetupNonInteractiveConfigReader.LinuxStatxBuffer>(nameof(SetupNonInteractiveConfigReader.LinuxStatxBuffer.DevMajor)).ToInt32());
    }

    [Fact]
    public void TryReadExact_rejects_short_reads()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var buffer = new byte[5];
        Assert.False(SetupNonInteractiveConfigReader.TryReadExact(stream, buffer));
    }

    [Fact]
    public void TryReadExact_accepts_full_content_across_partial_reads()
    {
        using var stream = new PartialReadStream([1, 2, 3, 4, 5], chunkSize: 2);
        var buffer = new byte[5];
        Assert.True(SetupNonInteractiveConfigReader.TryReadExact(stream, buffer));
        Assert.Equal([1, 2, 3, 4, 5], buffer);
    }

    [Fact]
    public void MacOS_and_unsupported_hosts_fail_closed_without_path_fallback()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            return;
        }

        var path = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());
        var outcome = SetupNonInteractiveConfigReader.Read(new HostSetupFileSystem(), path);
        Assert.False(outcome.Succeeded);
        Assert.Equal(SetupNonInteractiveResultCode.UnsupportedPlatform, outcome.FailureCode);
    }

    [Fact]
    public void Owner_only_config_round_trips_on_supported_hosts()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var json = SetupNonInteractiveTestSupport.BuildLocalMailpitJson();
        var path = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(json);
        var outcome = SetupNonInteractiveConfigReader.Read(new HostSetupFileSystem(), path);
        Assert.True(outcome.Succeeded);
        Assert.Equal(json.Trim(), SetupNonInteractiveConfigReader.DecodeUtf8(outcome.Content, out var valid).Trim());
        Assert.True(valid);
    }

    private sealed class PartialReadStream : MemoryStream
    {
        private readonly int _chunkSize;

        public PartialReadStream(byte[] buffer, int chunkSize)
            : base(buffer)
        {
            _chunkSize = chunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, _chunkSize));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length > _chunkSize)
            {
                buffer = buffer[.._chunkSize];
            }

            return base.Read(buffer);
        }
    }
}
