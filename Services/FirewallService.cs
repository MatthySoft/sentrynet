using System.Runtime.InteropServices;

namespace SentryNet.Services;

/// <summary>
/// Manages per-exe outbound block rules in the standard Windows Firewall rule store
/// (the same one netsh and WF.msc edit) via the HNetCfg COM API. Rules are named
/// after the exe path so they can be recognized and removed later. Reads work
/// unelevated; adding/removing rules throws UnauthorizedAccessException without admin.
/// </summary>
public sealed class FirewallService
{
    const string Prefix = "SentryNet block: ";
    const int FW_ACTION_BLOCK = 0;        // NET_FW_ACTION_BLOCK
    const int FW_DIR_OUT = 2;             // NET_FW_RULE_DIR_OUT
    const int FW_PROFILES_ALL = 0x7FFFFFFF;

    // Dispatch-only interop (DISPIDs from netfw.idl), so only the members
    // actually used need declaring — no full-vtable definitions.

    [ComImport, Guid("98325047-C671-4174-8D81-DEFCD3F03186"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface INetFwPolicy2
    {
        [DispId(7)] INetFwRules Rules { get; }
    }

    [ComImport, Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface INetFwRules
    {
        [DispId(2)] void Add(INetFwRule rule);
        [DispId(3)] void Remove(string name);
        [DispId(4)] INetFwRule Item(string name);
    }

    [ComImport, Guid("AF230D27-BABA-4E42-ACED-F524F22CFCE2"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface INetFwRule
    {
        [DispId(1)] string Name { get; set; }
        [DispId(2)] string Description { get; set; }
        [DispId(3)] string ApplicationName { get; set; }
        [DispId(11)] int Direction { get; set; }
        [DispId(14)] bool Enabled { get; set; }
        [DispId(15)] string Grouping { get; set; }
        [DispId(16)] int Profiles { get; set; }
        [DispId(18)] int Action { get; set; }
    }

    INetFwPolicy2? _policy;

    INetFwRules Rules =>
        (_policy ??= (INetFwPolicy2)Activator.CreateInstance(
            Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!).Rules;

    static string RuleName(string exePath) => Prefix + exePath;

    /// <summary>True if a SentryNet block rule exists for this exe.</summary>
    public bool IsBlocked(string exePath)
    {
        try { _ = Rules.Item(RuleName(exePath)); return true; }
        catch { return false; }
    }

    /// <summary>Adds an all-profiles, all-protocols outbound block rule for the exe.</summary>
    public void Block(string exePath)
    {
        var rule = (INetFwRule)Activator.CreateInstance(
            Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        rule.Name = RuleName(exePath);
        rule.Description = "Added by SentryNet. Blocks all outbound traffic for this program.";
        rule.ApplicationName = exePath;
        rule.Direction = FW_DIR_OUT;
        rule.Grouping = "SentryNet";
        rule.Profiles = FW_PROFILES_ALL;
        rule.Action = FW_ACTION_BLOCK;
        rule.Enabled = true;
        Rules.Add(rule);
    }

    /// <summary>Removes the SentryNet block rule for the exe (no-op if absent).</summary>
    public void Unblock(string exePath) => Rules.Remove(RuleName(exePath));
}
