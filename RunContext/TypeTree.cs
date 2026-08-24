using System;
using System.Collections.Generic;
using System.IO;
using Ranorex.Core.Reporting;

namespace RanorexOrangebeardListener.RunContext
{
    class TypeTree
    {
        public readonly string Type;
        internal readonly string Name = "";

        // The Ranorex Activity that was ActivityStack.Current when this node was created.
        // Must be captured at start time: by the time the matching finish event reaches us,
        // Ranorex has already popped ActivityStack.Current to the parent, so re-reading it
        // there would return the wrong (parent) activity and its unrelated sibling children.
        internal readonly Activity RanorexActivity;

        private TypeTree parent = null;
        private readonly List<TypeTree> children = new List<TypeTree>();

        internal IReadOnlyList<TypeTree> Children => children;

        internal TypeTree(string type, string name, Activity activity)
        {
            this.Type = type;
            this.Name = name;
            this.RanorexActivity = activity;
        }

        internal TypeTree Add(string type, string name, Activity activity)
        {
            TypeTree child = new TypeTree(type, name, activity);
            children.Add(child);
            child.parent = this;
            return child;
        }

        internal TypeTree GetParent()
        {
            return parent;
        }

        internal TypeTree GetRoot()
        {
            return parent == null ? this : parent.GetRoot();
        }

        internal void Print(string folder)
        {
            string timeStr = DateTime.Now.ToString("HHmmss");
            string filename = Path.Combine(folder, $"{timeStr}tree.log");
            using (StreamWriter outputFile = new StreamWriter(filename))
            {
                Print(outputFile, 0);
            }
        }

        private void Print(StreamWriter target, int indentation)
        {
            target.WriteLine();
            target.Write(new string(' ', indentation));
            target.Write($"{Type} {Name}");

            children.ForEach(child => child.Print(target, indentation + 2));
        }
    }
}
