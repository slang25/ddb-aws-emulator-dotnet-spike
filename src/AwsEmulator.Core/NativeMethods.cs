using System.Runtime.InteropServices;

namespace AwsEmulator;

/// <summary>P/Invoke surface of the Rust <c>aws-emulator-ffi</c> cdylib.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Library = "aws_emulator_ffi";

    [LibraryImport(Library, EntryPoint = "aws_emulator_new")]
    internal static partial EmulatorSafeHandle EmulatorNew();

    [LibraryImport(Library, EntryPoint = "aws_emulator_free")]
    internal static partial void EmulatorFree(nint emulator);

    /// <summary>
    /// Copies the request, returns immediately, and later invokes <paramref name="callback"/> exactly
    /// once on a native worker thread. The response pointers are only valid during the callback.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "aws_emulator_send")]
    internal static partial void EmulatorSend(
        EmulatorSafeHandle emulator,
        byte* method, nuint methodLength,
        byte* uri, nuint uriLength,
        byte* headers, nuint headersLength,
        byte* body, nuint bodyLength,
        delegate* unmanaged[Cdecl]<nint, ushort, byte*, nuint, byte*, nuint, void> callback,
        nint context);
}

internal sealed class EmulatorSafeHandle : SafeHandle
{
    public EmulatorSafeHandle() : base(nint.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.EmulatorFree(handle);
        return true;
    }
}
