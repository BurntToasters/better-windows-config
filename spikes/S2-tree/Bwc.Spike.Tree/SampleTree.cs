using System;
using System.Collections.Generic;

namespace Bwc.Spike.Tree
{
    /// <summary>
    /// Registry-shaped synthetic data: Computer > HKEY_CLASSES_ROOT > N keys named ".000001".."N".
    /// Every 4th key has 3 lazily generated children. The key at N/2 has 10,000 lazily generated children
    /// and is the node the bench expands and collapses.
    /// </summary>
    public sealed class SampleTree
    {
        public const int BigChildCount = 10_000;

        private readonly int _keyCount;
        private readonly int _bigIndex;

        public SampleTree(int keyCount)
        {
            _keyCount = Math.Max(1, keyCount);
            _bigIndex = _keyCount / 2;

            Root = new TreeRow("Computer", 0, null, hasChildren: true, loader: null);
            Hkcr = new TreeRow("HKEY_CLASSES_ROOT", 1, Root, hasChildren: true, loader: null);
            Root.Children = new List<TreeRow> { Hkcr };
            Hkcr.Children = MakeKeys(Hkcr);
        }

        public TreeRow Root { get; }
        public TreeRow Hkcr { get; }
        public TreeRow BigNode => Hkcr.Children![_bigIndex];

        private List<TreeRow> MakeKeys(TreeRow parent)
        {
            var list = new List<TreeRow>(_keyCount);
            for (int k = 0; k < _keyCount; k++)
            {
                bool isBig = k == _bigIndex;
                bool hasKids = isBig || k % 4 == 0;
                Func<TreeRow, List<TreeRow>>? loader = null;
                if (isBig) loader = MakeBigItems;
                else if (hasKids) loader = MakeFewItems;
                list.Add(new TreeRow("." + (k + 1).ToString("D6"), parent.Depth + 1, parent, hasKids, loader));
            }
            return list;
        }

        private static List<TreeRow> MakeFewItems(TreeRow parent) => MakeLeaves(parent, 3, "Key ");

        private static List<TreeRow> MakeBigItems(TreeRow parent) => MakeLeaves(parent, BigChildCount, "Item ");

        private static List<TreeRow> MakeLeaves(TreeRow parent, int count, string prefix)
        {
            var list = new List<TreeRow>(count);
            for (int j = 1; j <= count; j++)
            {
                list.Add(new TreeRow(prefix + j.ToString("D5"), parent.Depth + 1, parent, false, null));
            }
            return list;
        }
    }
}
