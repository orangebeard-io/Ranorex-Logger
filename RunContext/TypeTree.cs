using System;
using System.Collections.Generic;
using System.IO;

namespace RanorexOrangebeardListener.RunContext
{
    class TypeTree
    {
        public readonly string Type;
        private readonly string Name = "";

        private TypeTree parent = null;
        private readonly List<TypeTree> children = new List<TypeTree>();

        internal TypeTree(string type, string name)
        {
            this.Type = type;
            this.Name = name;
        }

        internal TypeTree Add(string type, string name)
        {
            TypeTree child = new TypeTree(type, name);
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
