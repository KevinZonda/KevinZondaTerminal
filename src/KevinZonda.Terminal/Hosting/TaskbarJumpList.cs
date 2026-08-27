using System.Runtime.InteropServices;
using System.Text;

namespace KevinZonda.Terminal.Hosting;

internal static class TaskbarJumpList
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const ushort VariantString = 31;
    private const int ShowNormal = 1;
    private const int MaximumPathCharacters = 32_768;
    private const string CategoryName = "Recent Workspaces";
    private static readonly Guid ShellLinkInterfaceId =
        new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid ObjectArrayInterfaceId =
        new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly PropertyKey TitleProperty = new(
        new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        2);

    internal static IReadOnlySet<string> Update(IReadOnlyList<string> workspaces)
    {
        var destinationListObject = (object)new DestinationListComObject();
        var destinationList = (ICustomDestinationList)destinationListObject;
        object? removedObject = null;
        var listStarted = false;
        var listCommitted = false;
        try
        {
            var objectArrayInterfaceId = ObjectArrayInterfaceId;
            ThrowIfFailed(destinationList.BeginList(
                out var maximumSlots,
                ref objectArrayInterfaceId,
                out removedObject));
            listStarted = true;
            if (removedObject is not IObjectArray removedItems)
            {
                throw new InvalidCastException(
                    "Windows did not return the Jump List removal array.");
            }
            var removed = FindRemovedWorkspaces(
                removedItems,
                workspaces);
            var visible = workspaces
                .Where(workspace => !removed.Contains(workspace))
                .Take(maximumSlots == 0
                    ? RecentWorkspaceStore.MaximumWorkspaces
                    : Math.Min((int)maximumSlots, RecentWorkspaceStore.MaximumWorkspaces))
                .ToArray();

            if (visible.Length > 0)
            {
                var collectionObject = (object)new EnumerableObjectCollectionComObject();
                try
                {
                    var collection = (IObjectCollection)collectionObject;
                    foreach (var workspace in visible)
                    {
                        var linkObject = CreateWorkspaceLink(workspace);
                        try
                        {
                            ThrowIfFailed(collection.AddObject(linkObject));
                        }
                        finally
                        {
                            ReleaseComObject(linkObject);
                        }
                    }

                    var appendResult = destinationList.AppendCategory(
                        CategoryName,
                        (IObjectArray)collectionObject);
                    if (appendResult != AccessDenied)
                    {
                        ThrowIfFailed(appendResult);
                    }
                }
                finally
                {
                    ReleaseComObject(collectionObject);
                }
            }

            ThrowIfFailed(destinationList.CommitList());
            listCommitted = true;
            return removed;
        }
        finally
        {
            if (listStarted && !listCommitted)
            {
                _ = destinationList.AbortList();
            }
            if (removedObject is not null)
            {
                ReleaseComObject(removedObject);
            }
            ReleaseComObject(destinationListObject);
        }
    }

    private static HashSet<string> FindRemovedWorkspaces(
        IObjectArray removedItems,
        IReadOnlyList<string> workspaces)
    {
        var identities = workspaces.ToDictionary(
            CreateIdentity,
            workspace => workspace,
            WorkspaceLinkIdentityComparer.Instance);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ThrowIfFailed(removedItems.GetCount(out var count));
        for (uint index = 0; index < count; index++)
        {
            object? removedObject = null;
            try
            {
                var shellLinkInterfaceId = ShellLinkInterfaceId;
                var result = removedItems.GetAt(
                    index,
                    ref shellLinkInterfaceId,
                    out removedObject);
                if (result < 0 || removedObject is not IShellLinkW link)
                {
                    continue;
                }

                var target = new StringBuilder(MaximumPathCharacters);
                var arguments = new StringBuilder(MaximumPathCharacters);
                if (link.GetPath(target, target.Capacity, IntPtr.Zero, 0) >= 0 &&
                    link.GetArguments(arguments, arguments.Capacity) >= 0)
                {
                    var identity = new WorkspaceLinkIdentity(
                        NormalizePath(target.ToString()),
                        arguments.ToString());
                    if (identities.TryGetValue(identity, out var workspace))
                    {
                        removed.Add(workspace);
                    }
                }
            }
            finally
            {
                if (removedObject is not null)
                {
                    ReleaseComObject(removedObject);
                }
            }
        }
        return removed;
    }

    private static object CreateWorkspaceLink(string workspace)
    {
        var startInfo = SelfProcessLauncher.CreateStartInfo(workspace, [workspace]);
        var targetPath = Path.GetFullPath(startInfo.FileName);
        var arguments = string.Join(' ', startInfo.ArgumentList.Select(QuoteArgument));
        var linkObject = (object)new ShellLinkComObject();
        try
        {
            var link = (IShellLinkW)linkObject;
            ThrowIfFailed(link.SetPath(targetPath));
            ThrowIfFailed(link.SetArguments(arguments));
            ThrowIfFailed(link.SetWorkingDirectory(workspace));
            ThrowIfFailed(link.SetDescription($"Open KTerm in {workspace}"));
            ThrowIfFailed(link.SetIconLocation(targetPath, 0));
            ThrowIfFailed(link.SetShowCmd(ShowNormal));

            var propertyStore = (IPropertyStore)linkObject;
            var title = PropVariant.FromString(workspace);
            var titleProperty = TitleProperty;
            try
            {
                ThrowIfFailed(propertyStore.SetValue(ref titleProperty, ref title));
                ThrowIfFailed(propertyStore.Commit());
            }
            finally
            {
                _ = PropVariantClear(ref title);
            }
            return linkObject;
        }
        catch
        {
            ReleaseComObject(linkObject);
            throw;
        }
    }

    private static WorkspaceLinkIdentity CreateIdentity(string workspace)
    {
        var startInfo = SelfProcessLauncher.CreateStartInfo(workspace, [workspace]);
        return new WorkspaceLinkIdentity(
            NormalizePath(startInfo.FileName),
            string.Join(' ', startInfo.ArgumentList.Select(QuoteArgument)));
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);

    private readonly record struct WorkspaceLinkIdentity(string TargetPath, string Arguments);

    private sealed class WorkspaceLinkIdentityComparer : IEqualityComparer<WorkspaceLinkIdentity>
    {
        internal static WorkspaceLinkIdentityComparer Instance { get; } = new();

        public bool Equals(WorkspaceLinkIdentity left, WorkspaceLinkIdentity right) =>
            string.Equals(left.TargetPath, right.TargetPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Arguments, right.Arguments, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(WorkspaceLinkIdentity value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.TargetPath),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Arguments));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        internal Guid FormatId;
        internal uint PropertyId;

        internal PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        private ushort _type;
        [FieldOffset(8)]
        private IntPtr _value;

        internal static PropVariant FromString(string value) => new()
        {
            _type = VariantString,
            _value = Marshal.StringToCoTaskMemUni(value)
        };
    }

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    private sealed class DestinationListComObject;

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    private sealed class EnumerableObjectCollectionComObject;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkComObject;

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        [PreserveSig]
        int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [PreserveSig]
        int BeginList(
            out uint maximumSlots,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object removedItems);

        [PreserveSig]
        int AppendCategory(
            [MarshalAs(UnmanagedType.LPWStr)] string category,
            IObjectArray objects);

        [PreserveSig]
        int AppendKnownCategory(int category);

        [PreserveSig]
        int AddUserTasks(IObjectArray objects);

        [PreserveSig]
        int CommitList();

        [PreserveSig]
        int GetRemovedDestinations(
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object removedItems);

        [PreserveSig]
        int DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? appId);

        [PreserveSig]
        int AbortList();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(
            uint index,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object value);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection : IObjectArray
    {
        [PreserveSig]
        new int GetCount(out uint count);

        [PreserveSig]
        new int GetAt(
            uint index,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object value);

        [PreserveSig]
        int AddObject([MarshalAs(UnmanagedType.IUnknown)] object value);

        [PreserveSig]
        int AddFromArray(IObjectArray source);

        [PreserveSig]
        int RemoveObjectAt(uint index);

        [PreserveSig]
        int Clear();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int characters,
            IntPtr findData,
            uint flags);

        [PreserveSig]
        int GetIdList(out IntPtr itemIdList);

        [PreserveSig]
        int SetIdList(IntPtr itemIdList);

        [PreserveSig]
        int GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description,
            int characters);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

        [PreserveSig]
        int GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int characters);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        [PreserveSig]
        int GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int characters);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        [PreserveSig]
        int GetHotkey(out ushort hotkey);

        [PreserveSig]
        int SetHotkey(ushort hotkey);

        [PreserveSig]
        int GetShowCmd(out int showCommand);

        [PreserveSig]
        int SetShowCmd(int showCommand);

        [PreserveSig]
        int GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int characters,
            out int iconIndex);

        [PreserveSig]
        int SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            int iconIndex);

        [PreserveSig]
        int SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string relativePath,
            uint reserved);

        [PreserveSig]
        int Resolve(IntPtr window, uint flags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }
}
