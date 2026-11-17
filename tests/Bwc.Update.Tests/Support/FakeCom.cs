using System.Runtime.InteropServices;

namespace Bwc.Update.Tests;

internal unsafe delegate int QueryInterfaceFn(nint self, Guid* iid, nint* result);

internal delegate uint RefCountFn(nint self);

internal unsafe delegate int FindWindowSwFn(nint self, ComVariant* location, ComVariant* root, int windowClass, int* handle, int options, nint* dispatch);

internal unsafe delegate int QueryServiceFn(nint self, Guid* service, Guid* iid, nint* result);

internal unsafe delegate int OutPointerFn(nint self, nint* result);

internal unsafe delegate int GetItemObjectFn(nint self, uint item, Guid* iid, nint* result);

internal unsafe delegate int ShellExecuteFn(nint self, nint file, ComVariant* args, ComVariant* directory, ComVariant* operation, ComVariant* show);

/// <summary> A COM object made of native memory: a pointer to a vtable of function pointers, with a reference count
/// that the tests check. <see cref="HandOut"/> gives the code under test a reference it must release exactly once.
/// </summary>
internal sealed unsafe class FakeCom : IDisposable
{
    private const int SlotCount = 48;
    private const int ENoInterface = unchecked((int)0x80004002);
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private readonly nint* _block;
    private readonly nint* _vtable;
    private readonly List<Delegate> _keep = [];
    private readonly HashSet<Guid> _interfaces;

    public FakeCom(string name, params Guid[] interfaces)
    {
        Name = name;
        _interfaces = [IidUnknown, .. interfaces];
        _block = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        _vtable = (nint*)NativeMemory.AllocZeroed((nuint)(SlotCount * sizeof(nint)));
        *_block = (nint)_vtable;
        Set(0, new QueryInterfaceFn(QueryInterface));
        Set(1, new RefCountFn(AddRef));
        Set(2, new RefCountFn(Release));
    }

    public string Name { get; }

    /// <summary>References given out (by <see cref="HandOut"/>, a successful QueryInterface, or AddRef).</summary>
    public int Handouts { get; private set; }

    public int Releases { get; private set; }

    /// <summary>True when a release came with no reference outstanding: a double release.</summary>
    public bool OverReleased { get; private set; }

    public nint Pointer => (nint)_block;

    public nint HandOut()
    {
        Handouts++;
        return (nint)_block;
    }

    public void Set<T>(int slot, T function)
        where T : Delegate
    {
        _keep.Add(function);
        _vtable[slot] = Marshal.GetFunctionPointerForDelegate(function);
    }

    public string Balance() => $"{Name}: {Handouts} handed out, {Releases} released{(OverReleased ? ", RELEASED TOO OFTEN" : string.Empty)}";

    public bool IsBalanced => Handouts == Releases && !OverReleased;

    public void Dispose()
    {
        NativeMemory.Free(_vtable);
        NativeMemory.Free(_block);
    }

    private int QueryInterface(nint self, Guid* iid, nint* result)
    {
        if (_interfaces.Contains(*iid))
        {
            *result = HandOut();
            return 0;
        }

        *result = 0;
        return ENoInterface;
    }

    private uint AddRef(nint self)
    {
        Handouts++;
        return (uint)(Handouts - Releases);
    }

    private uint Release(nint self)
    {
        Releases++;
        if (Releases > Handouts)
        {
            OverReleased = true;
        }

        return (uint)Math.Max(0, Handouts - Releases);
    }
}
