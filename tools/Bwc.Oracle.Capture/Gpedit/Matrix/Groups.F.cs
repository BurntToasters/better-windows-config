using Bwc.Oracle.Capture.Gpedit.Driver;
using static Bwc.Oracle.Capture.Gpedit.Matrix.MatrixOps;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal static partial class MatrixGroups
{
    // Group F: visuals. Structure that does not depend on the machine's own templates is recorded in case.json (BWC nodes only); everything
    // else (screenshots, Microsoft node names, menu text, counts of system templates) stays in the untracked log.
    internal static IEnumerable<MatrixCase> GroupF(MatrixContext c)
    {
        FixtureModel f = c.Fixture;
        string[] columns = { "Setting", "State", "Comment" };
        string[] topMenus = { "File", "Action", "View", "Help" };
        string[] adminTemplates = { "Administrative Templates" };
        string[] allSettings = { "Administrative Templates", "All Settings" };
        string[] basics = f.Policy("Pol_Class_Machine").NodePath.ToArray();
        string[] sortCases = f.Policy("Pol_Sort_Zulu").NodePath.ToArray();
        string emptyCategory = f.Categories.First(x => x.Name == "EmptyCategory").Display;

        // Tree order and icons: children of every node, then the tree's image indexes.
        foreach (string root in new[] { "Computer", "User" })
        {
            var tree = new CaseBuilder("F", "Tree_" + root, $"tree order under {root} Configuration: every fixture node and the placeholder nodes, then image indexes");
            tree.Add(Shot("start"));
            tree.Add(Children(root, Array.Empty<string>(), "children of the root configuration node"));
            tree.Add(Children(root, new[] { "Software Settings" }), isOptional: true);
            tree.Add(Children(root, new[] { "Windows Settings" }), isOptional: true);
            tree.Add(Children(root, adminTemplates, "children of Administrative Templates"));
            foreach (List<string> node in f.NodePaths(root))
            {
                tree.Add(Children(root, node));
            }

            tree.Add(Children(root, new[] { "Administrative Templates", "BWC Oracle", emptyCategory }, "the category that holds no policy"), isOptional: true);
            tree.Add(Plain("readLayout", "tree image indexes and layout"), isOptional: true);
            tree.Add(Shot("tree-expanded"));
            yield return tree.Build();
        }

        // The result list of every node: columns, rows, State column, status bar text.
        foreach (string root in new[] { "Computer", "User" })
        {
            var nodes = new CaseBuilder("F", "Nodes_" + root, $"result list and status bar of every fixture node under {root} Configuration, nothing configured");
            foreach (List<string> node in f.NodePaths(root))
            {
                nodes.Add(Select(root, node));
                nodes.Add(ReadList(root, node));
                nodes.Add(Status(root, node));
            }

            yield return nodes.Build();
        }

        // Placeholder nodes and the top nodes.
        yield return new CaseBuilder("F", "Placeholders_Computer", "the top nodes and the placeholder nodes under Computer Configuration")
            .Add(Select("Computer", Array.Empty<string>(), "Computer Configuration"))
            .Add(ReadList("Computer", Array.Empty<string>()))
            .Add(Status("Computer", Array.Empty<string>()))
            .Add(Shot("computer-configuration"))
            .Add(Select("Computer", new[] { "Software Settings" }), isOptional: true)
            .Add(ReadList("Computer", new[] { "Software Settings" }), isOptional: true)
            .Add(Shot("software-settings"))
            .Add(Select("Computer", new[] { "Windows Settings" }), isOptional: true)
            .Add(ReadList("Computer", new[] { "Windows Settings" }), isOptional: true)
            .Add(Shot("windows-settings"))
            .Add(Select("Computer", adminTemplates), isOptional: true)
            .Add(Status("Computer", adminTemplates), isOptional: true)
            .Add(Shot("administrative-templates"))
            .Build();
        yield return new CaseBuilder("F", "Placeholders_User", "the top nodes and the placeholder nodes under User Configuration")
            .Add(Select("User", Array.Empty<string>(), "User Configuration"))
            .Add(ReadList("User", Array.Empty<string>()))
            .Add(Status("User", Array.Empty<string>()))
            .Add(Select("User", new[] { "Software Settings" }), isOptional: true)
            .Add(ReadList("User", new[] { "Software Settings" }), isOptional: true)
            .Add(Select("User", new[] { "Windows Settings" }), isOptional: true)
            .Add(ReadList("User", new[] { "Windows Settings" }), isOptional: true)
            .Add(Shot("windows-settings"))
            .Build();

        // All Settings and Extended/Standard.
        foreach (string root in new[] { "Computer", "User" })
        {
            yield return new CaseBuilder("F", "AllSettings_" + root, $"the All Settings node under {root} Configuration: status bar, headers, first rows (counts stay untracked)")
                .Add(Select(root, allSettings))
                .Add(Status(root, allSettings))
                .Add(ReadList(root, allSettings, namesOnly: true, maxRows: 60))
                .Add(Plain("readLayout", "layout at All Settings"), isOptional: true)
                .Add(Shot("all-settings"))
                .Build();
        }

        yield return new CaseBuilder("F", "Tabs_Extended_Standard", "Extended and Standard tabs on a node and on All Settings")
            .Add(Select("Computer", basics))
            .Add(new GpeditOp { Kind = "selectTab", Tab = "Standard" }, isOptional: true)
            .Add(ReadList("Computer", basics))
            .Add(Shot("basics-standard"))
            .Add(new GpeditOp { Kind = "selectTab", Tab = "Extended" }, isOptional: true)
            .Add(ReadList("Computer", basics))
            .Add(Shot("basics-extended"))
            .Add(Select("Computer", allSettings))
            .Add(new GpeditOp { Kind = "selectTab", Tab = "Standard" }, isOptional: true)
            .Add(Shot("all-settings-standard"))
            .Add(new GpeditOp { Kind = "selectTab", Tab = "Extended" }, isOptional: true)
            .Add(Shot("all-settings-extended"))
            .Build();

        // Sort: eight sort policies with a mix of states and comments; each column clicked three times.
        PolicyInfo zulu = f.Policy("Pol_Sort_Zulu");
        PolicyInfo lowerAlpha = f.Policy("Pol_Sort_LowerAlpha");
        PolicyInfo digit10 = f.Policy("Pol_Sort_Digit10");
        PolicyInfo digit2 = f.Policy("Pol_Sort_Digit2");

        CaseBuilder Configure(CaseBuilder b)
        {
            b.Add(Edit(zulu, "Computer", "Enabled"));
            b.Add(Edit(lowerAlpha, "Computer", "Disabled"));
            b.Add(Edit(digit10, "Computer", "Not Configured", "bwc comment one"));
            b.Add(Edit(digit2, "Computer", "Enabled", "bwc comment two"));
            return b;
        }

        CaseBuilder Cleanup(CaseBuilder b)
        {
            b.Add(Edit(zulu, "Computer", "Not Configured", press: new[] { "OK" }));
            b.Add(Edit(lowerAlpha, "Computer", "Not Configured", press: new[] { "OK" }));
            b.Add(Edit(digit10, "Computer", "Not Configured", string.Empty));
            b.Add(Edit(digit2, "Computer", "Not Configured", string.Empty));
            b.Mark("cleaned");
            return b;
        }

        var sort = Configure(new CaseBuilder("F", "Sort_Columns", "sort cases node: every column clicked three times, with Enabled, Disabled and commented rows"));
        sort.Add(Select("Computer", sortCases));
        sort.Add(ReadList("Computer", sortCases, label: "default order"));
        foreach (string column in columns)
        {
            for (int click = 1; click <= 3; click++)
            {
                sort.Add(new GpeditOp { Kind = "sortColumn", Root = "Computer", Path = sortCases.ToList(), Column = column, Label = $"{column} click {click}" });
            }
        }

        sort.Add(ReadList("Computer", sortCases, label: "order after the last click"));
        sort.Add(Select("Computer", basics));
        sort.Add(ReadList("Computer", basics, label: "another node keeps its own sort"));
        Cleanup(sort);
        yield return sort.Build();

        // Filter: options, results and clearing.
        var filter = Configure(new CaseBuilder("F", "Filter_Options", "Filter Options on the sort cases node: managed, configured, commented, keywords, requirements; results after each"));
        filter.Add(Select("Computer", sortCases));
        filter.Add(ReadList("Computer", sortCases, label: "unfiltered"));
        void Variant(string label, FilterSpec spec)
        {
            filter.Add(new GpeditOp { Kind = "setFilter", Root = "Computer", Path = sortCases.ToList(), Filter = spec, Label = label }, isOptional: true);
            filter.Add(new GpeditOp { Kind = "filterOn", Root = "Computer", Path = sortCases.ToList(), FilterOn = true, Label = label + ": Filter On" }, isOptional: true);
            filter.Add(ReadList("Computer", sortCases, label: label + ": rows"), isOptional: true);
            filter.Add(new GpeditOp { Kind = "clearFilter", Root = "Computer", Path = sortCases.ToList(), Label = label + ": cleared" }, isOptional: true);
        }

        Variant("configured yes", new FilterSpec { Configured = "Yes" });
        Variant("configured no", new FilterSpec { Configured = "No" });
        Variant("commented yes", new FilterSpec { Commented = "Yes" });
        Variant("commented no", new FilterSpec { Commented = "No" });
        Variant("managed yes", new FilterSpec { Managed = "Yes" });
        Variant("managed no", new FilterSpec { Managed = "No" });
        Variant("configured yes and commented yes", new FilterSpec { Configured = "Yes", Commented = "Yes" });
        Variant("keyword zulu in title", new FilterSpec { KeywordsEnabled = true, Keywords = "zulu", InTitle = true, InHelp = false, InComment = false });
        Variant("keyword sort in help", new FilterSpec { KeywordsEnabled = true, Keywords = "sort", InTitle = false, InHelp = true, InComment = false });
        Variant("keyword comment in comment", new FilterSpec { KeywordsEnabled = true, Keywords = "comment", InTitle = false, InHelp = false, InComment = true });
        Variant("keyword policy in title", new FilterSpec { KeywordsEnabled = true, Keywords = "policy", InTitle = true, InHelp = false, InComment = false });
        Variant("keyword two words match any", new FilterSpec { KeywordsEnabled = true, Keywords = "zulu alpha", Match = "Any", InTitle = true });
        Variant("keyword two words match all", new FilterSpec { KeywordsEnabled = true, Keywords = "policy sort", Match = "All", InTitle = true, InHelp = true });
        Variant("keyword exact", new FilterSpec { KeywordsEnabled = true, Keywords = "Policy 2", Match = "Exact", InTitle = true });
        Variant("requirements none", new FilterSpec { RequirementsEnabled = true, Platforms = "none" });
        Variant("requirements all", new FilterSpec { RequirementsEnabled = true, Platforms = "all" });
        filter.Add(new GpeditOp { Kind = "setFilter", Root = "Computer", Path = sortCases.ToList(), Filter = new FilterSpec { Configured = "Yes" }, Label = "configured yes, kept on" }, isOptional: true);
        filter.Add(Select("Computer", basics));
        filter.Add(ReadList("Computer", basics, label: "filter stays on in another node"), isOptional: true);
        filter.Add(Select("Computer", allSettings));
        filter.Add(Status("Computer", allSettings), isOptional: true);
        filter.Add(new GpeditOp { Kind = "clearFilter", Root = "Computer", Path = allSettings.ToList(), Label = "cleared at the end" }, isOptional: true);
        Cleanup(filter);
        yield return filter.Build();

        // Menus and context menus per node and per row.
        foreach (string root in new[] { "Computer", "User" })
        {
            var menus = new CaseBuilder("F", "Menus_" + root, $"main menus and context menus on each node kind and on a row under {root} Configuration");
            (string Name, string[] Path, bool Optional)[] nodes =
            {
                ("root configuration node", Array.Empty<string>(), false),
                ("Administrative Templates", adminTemplates, false),
                ("BWC Oracle category", new[] { "Administrative Templates", "BWC Oracle" }, false),
                ("category with policies", basics, false),
                ("All Settings", allSettings, false),
                ("Software Settings placeholder", new[] { "Software Settings" }, true),
                ("Windows Settings placeholder", new[] { "Windows Settings" }, true),
            };
            foreach ((string name, string[] path, bool optional) in nodes)
            {
                menus.Add(Select(root, path, name), isOptional: optional);
                foreach (string menu in topMenus)
                {
                    menus.Add(Menu(menu, label: $"{menu} menu at {name}"), isOptional: optional);
                }

                menus.Add(Menu("Context", root, path, label: "context menu at " + name), isOptional: true);
            }

            PolicyInfo row = root == "Computer" ? f.Policy("Pol_Class_Machine") : f.Policy("Pol_Class_User");
            menus.Add(Menu("Context", root, row.NodePath, row.DisplayName, "context menu of a policy row"));
            foreach (string menu in topMenus)
            {
                menus.Add(Menu(menu, label: $"{menu} menu with a policy row selected"));
            }

            yield return menus.Build();
        }

        // Toolbar buttons and their enable state per node kind and with a row selected.
        yield return new CaseBuilder("F", "Toolbar_States", "toolbar buttons and enable state on each node kind and with a row selected (Computer)")
            .Add(Select("Computer", Array.Empty<string>()))
            .Add(Plain("readLayout", "root configuration node"), isOptional: true)
            .Add(Select("Computer", adminTemplates))
            .Add(Plain("readLayout", "Administrative Templates"), isOptional: true)
            .Add(Select("Computer", basics))
            .Add(Plain("readLayout", "category with policies"), isOptional: true)
            .Add(Menu("Context", "Computer", basics, f.Policy("Pol_Class_Machine").DisplayName, "select a row"))
            .Add(Plain("readLayout", "a row selected"), isOptional: true)
            .Add(Select("Computer", allSettings))
            .Add(Plain("readLayout", "All Settings"), isOptional: true)
            .Add(Select("Computer", new[] { "Software Settings" }), isOptional: true)
            .Add(Plain("readLayout", "placeholder node"), isOptional: true)
            .Add(Shot("toolbar"))
            .Build();

        // The setting dialog: control rectangles and tab order, by option kind and by state.
        var dialogs = new CaseBuilder("F", "Dialog_Layouts", "setting dialog control rectangles, tab stops and tab order for each option kind, Not Configured and Enabled");
        foreach (string name in new[]
        {
            "Pol_Class_Machine", "Pol_Amp_LabelSingle", "Pol_Decimal_MinMax", "Pol_LongDecimal_Basic", "Pol_Text_Plain", "Pol_Text_ComboBoxSuggestions",
            "Pol_MultiText_Basic", "Pol_Enum_DecimalItems", "Pol_List_Plain", "Pol_Bool_TwoChecksReverseOrder", "Pol_Explain_MultiParagraph",
        })
        {
            PolicyInfo policy = f.Policy(name);
            dialogs.Add(new GpeditOp { Kind = "dialogLayout", Root = "Computer", Path = policy.NodePath, Policy = policy.DisplayName, Label = name + " not configured" }, isOptional: true);
            dialogs.Add(new GpeditOp { Kind = "dialogLayout", Root = "Computer", Path = policy.NodePath, Policy = policy.DisplayName, State = "Enabled", Label = name + " enabled" }, isOptional: true);
        }

        dialogs.Add(Shot("done"));
        yield return dialogs.Build();

        // Previous and Next Setting at the ends of a node's list.
        List<PolicyInfo> inBasics = f.Policies.Where(p => p.Roots.Contains("Computer") && p.NodePath.SequenceEqual(f.Policy("Pol_Class_Machine").NodePath))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var ends = new CaseBuilder("F", "PrevNext_Ends", "Previous Setting and Next Setting from the first, a middle and the last policy of a node, and from the only policy of a node");
        foreach (PolicyInfo policy in new[] { inBasics[0], inBasics[inBasics.Count / 2], inBasics[^1] })
        {
            ends.Add(Edit(policy, "Computer", press: new[] { "Previous", "Cancel" }, label: "Previous from " + policy.Name), isOptional: true);
            ends.Add(Edit(policy, "Computer", press: new[] { "Next", "Cancel" }, label: "Next from " + policy.Name), isOptional: true);
            ends.Add(Edit(policy, "Computer", press: new[] { "Next", "Previous", "Cancel" }, label: "Next then Previous from " + policy.Name), isOptional: true);
        }

        PolicyInfo only = f.Policy("Pol_Top_DirectUnderRoot");
        ends.Add(Edit(only, "User", press: new[] { "Previous", "Cancel" }, label: "Previous from the only policy"), isOptional: true);
        ends.Add(Edit(only, "User", press: new[] { "Next", "Cancel" }, label: "Next from the only policy"), isOptional: true);
        yield return ends.Build();

        // Keys.
        string[] treeKeys =
        {
            "Left", "Right", "Down", "Up", "Home", "End", "PageDown", "PageUp", "Plus", "Minus", "Asterisk", "Backspace", "Tab", "Shift+Tab",
            "F5", "F6", "Esc", "Alt+Left", "Alt+Right", "Alt+Up", "Apps",
        };
        string[] listKeys = { "Down", "Up", "Home", "End", "PageDown", "PageUp", "Space", "Right", "Left", "Ctrl+A", "F5", "Tab", "Shift+Tab", "Apps", "Enter" };
        yield return new CaseBuilder("F", "Keys_Tree", "keys in the console tree on the sort cases node")
            .Add(Select("Computer", sortCases))
            .Add(new GpeditOp { Kind = "pressKey", Root = "Computer", Path = sortCases.ToList(), Keys = treeKeys.ToList(), Name = "tree", Label = "tree keys" }, isOptional: true)
            .Add(Shot("after-tree-keys"))
            .Build();
        yield return new CaseBuilder("F", "Keys_List", "keys in the result list on the sort cases node, with a row selected")
            .Add(Select("Computer", sortCases))
            .Add(new GpeditOp
            {
                Kind = "pressKey",
                Root = "Computer",
                Path = sortCases.ToList(),
                Policy = zulu.DisplayName,
                Keys = listKeys.ToList(),
                Name = "list",
                Label = "list keys",
            }, isOptional: true)
            .Add(Shot("after-list-keys"))
            .Build();
        yield return new CaseBuilder("F", "Keys_ListAllSettings", "keys in the result list on the All Settings node")
            .Add(Select("Computer", allSettings))
            .Add(new GpeditOp { Kind = "pressKey", Root = "Computer", Path = allSettings.ToList(), Keys = new List<string> { "Down", "Down", "Up", "End", "Home", "PageDown" }, Name = "list", Label = "list keys" }, isOptional: true)
            .Build();
    }
}
