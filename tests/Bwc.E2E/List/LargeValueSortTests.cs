using System.Globalization;
using System.Runtime.InteropServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.List;

/// <summary>
/// One key holds 10,001 values (the default value and 10,000 named values). Above 10,000 rows the list sorts on the
/// thread pool, so this run checks that the background sort applied: the first row is (Default), the row count is
/// 10,001, and every realized row at its index matches the native collation order that the test computes itself.
/// The run key is removed in finally.
/// </summary>
[TestClass]
public sealed class LargeValueSortTests
{
    private const int NamedValueCount = 10_000;
    private const int TotalRows = NamedValueCount + 1;
    private const int WalkSteps = 14;
    private const int PagesPerStep = 25;
    private const string DefaultLabel = "(Default)";
    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void List_TenThousandValuesSortInBackground()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("list.many.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            SeedValues(sandbox);
            string[] expected = ExpectedOrder();

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("list-many"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, "Many");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "list.many.navigate");

            bool listed = ControlTestSupport.WaitUntil(() => RowAt(session, 0) is not null, TimeSpan.FromSeconds(30));
            E2EGuards.RequireSandboxNavigation(session, sandbox, listed, "list.many.list");

            CheckRowCount(session);
            CheckFirstRow(session);
            CheckWalk(session, expected);
            session.Close();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    private static void SeedValues(SandboxRegistry sandbox)
    {
        sandbox.SetString("Many", string.Empty, "default text");
        for (var number = 1; number <= NamedValueCount; number += 1)
        {
            sandbox.SetString("Many", "Item" + number.ToString("D5", CultureInfo.InvariantCulture), "v" + number.ToString(CultureInfo.InvariantCulture));
        }
    }

    // The default value first, then every name in the native collation order at the user locale, ignoring case.
    // The result holds the displayed names, so the unnamed value is (Default).
    private static string[] ExpectedOrder()
    {
        var names = new List<string> { string.Empty };
        for (var number = 1; number <= NamedValueCount; number += 1)
        {
            names.Add("Item" + number.ToString("D5", CultureInfo.InvariantCulture));
        }

        return names
            .OrderBy(name => name, Comparer<string>.Create(CompareNames))
            .Select(name => name.Length == 0 ? DefaultLabel : name)
            .ToArray();
    }

    private static int CompareNames(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length == right.Length ? 0 : left.Length == 0 ? -1 : 1;
        }

        // CompareStringW returns 1, 2, or 3 for less, equal, and greater.
        return NativeCollation.CompareStringW(LocaleUserDefault, NormIgnoreCase, left, left.Length, right, right.Length) - 2;
    }


    private static void CheckRowCount(AppSession session)
    {
        AutomationElement? list = session.Find(ControlTestSupport.ListControl);
        int rows = -1;
        if (list is not null && list.Patterns.Grid.IsSupported)
        {
            rows = list.Patterns.Grid.Pattern.RowCount.Value;
        }

        E2EResults.Record(
            "list.many.count",
            rows == TotalRows,
            $"The list's Grid pattern reports {rows} row(s); the key holds {TotalRows} values.");
    }

    private static void CheckFirstRow(AppSession session)
    {
        AutomationElement? first = RowAt(session, 0);
        string name = ControlTestSupport.SafeName(first);
        E2EResults.Record(
            "list.many.first",
            name == DefaultLabel,
            $"The first row is '{name}'; the first row must be the default value.");
    }

    // Walks the list with PageDown and checks every realized row against the expected order at its index.
    private static void CheckWalk(AppSession session, string[] expected)
    {
        var verified = new HashSet<int>();
        var mismatches = new List<string>();

        // The keys go to the list only when it has focus. Selecting the first row gives the list focus.
        ControlTestSupport.Select(RowAt(session, 0));
        _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(RowAt(session, 0)), TimeSpan.FromSeconds(5));
        for (var step = 0; step < WalkSteps; step += 1)
        {
            foreach ((int index, string name) in RealizedRows(session))
            {
                if (index < 0 || index >= expected.Length)
                {
                    mismatches.Add($"row {index} is outside the list");
                    continue;
                }

                _ = verified.Add(index);
                if (!string.Equals(name, expected[index], StringComparison.Ordinal) && mismatches.Count < 10)
                {
                    mismatches.Add($"row {index} is '{name}', expected '{expected[index]}'");
                }
            }

            ControlTestSupport.SendKeys(session, Enumerable.Repeat("NEXT", PagesPerStep).ToArray());
            _ = WaitForFilledRows(session);
        }

        ControlTestSupport.SendKeys(session, "END");
        _ = ControlTestSupport.WaitUntil(() => RealizedRows(session).Any(row => row.Index == expected.Length - 1), TimeSpan.FromSeconds(5));
        _ = WaitForFilledRows(session);
        (int lastIndex, string lastName) = RealizedRows(session).LastOrDefault();
        bool lastMatches = lastIndex == expected.Length - 1 && lastName == expected[^1];

        E2EResults.Record(
            "list.many.sorted",
            mismatches.Count == 0 && verified.Count >= WalkSteps,
            $"Checked {verified.Count} realized row position(s) across {WalkSteps} steps; mismatches: {(mismatches.Count == 0 ? "none" : string.Join("; ", mismatches))}.");
        E2EResults.Record(
            "list.many.last",
            lastMatches,
            $"After End the last realized row is index {lastIndex} '{lastName}'; expected index {expected.Length - 1} '{expected[^1]}'.");
    }

    private static List<(int Index, string Name)> RealizedRows(AppSession session)
    {
        var rows = new List<(int Index, string Name)>();
        foreach (AutomationElement row in ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem))
        {
            string id = ControlTestSupport.SafeId(row);
            if (id.StartsWith(ControlTestSupport.ListRowPrefix, StringComparison.Ordinal) &&
                int.TryParse(id.AsSpan(ControlTestSupport.ListRowPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                rows.Add((index, ControlTestSupport.SafeName(row)));
            }
        }

        return rows.OrderBy(row => row.Index).ToList();
    }

    // A row recycled by a scroll reads blank until the list fills it. Wait until at least one row is realized and
    // none is blank, so the walk never reads a recycled row. Polls UIA; there is no fixed sleep.
    private static bool WaitForFilledRows(AppSession session) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                List<(int Index, string Name)> rows = RealizedRows(session);
                return rows.Count > 0 && rows.All(row => row.Name.Length > 0);
            },
            TimeSpan.FromSeconds(5));

    private static AutomationElement? RowAt(AppSession session, int index)
    {
        string id = ControlTestSupport.ListRowPrefix + index.ToString(CultureInfo.InvariantCulture);
        return ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .FirstOrDefault(row => ControlTestSupport.SafeId(row) == id);
    }

    private const uint LocaleUserDefault = 0x0400;
    private const uint NormIgnoreCase = 0x00000001;

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Large list sort checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

/// <summary>The platform collation of the classic list, called directly so the expected order is independent.</summary>
internal static class NativeCollation
{
#pragma warning disable SYSLIB1054 // One P/Invoke does not justify LibraryImport plumbing in the test project.
    [DllImport("kernel32.dll", EntryPoint = "CompareStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CompareStringW(uint locale, uint flags, string left, int leftLength, string right, int rightLength);
#pragma warning restore SYSLIB1054
}
