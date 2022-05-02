using System;
using System.Collections.Generic;
using System.IO;
using Orangebeard.Client.Entities;

namespace RanorexOrangebeardListener
{
    class TypeTree
    {
        public TestItemType ItemType { get; private set; }
        private readonly string name = "";
        private TypeTree parent = null;
        private Guid? itemId = null;
        private readonly List<TypeTree> children = new List<TypeTree>();

        internal TypeTree(TestItemType itemType, string name, Guid? itemId)
        {
            this.ItemType = itemType;
            this.name = name;
            this.itemId = itemId;
        }

        internal TypeTree Add(TestItemType type, string name, Guid? itemId)
        {
            TypeTree child = new TypeTree(type, name, itemId);
            children.Add(child);
            child.parent = this;
            return child;
        }

        internal TypeTree GetParent()
        {
            return this.parent;
        }

        internal TypeTree GetRoot()
        {
            return (parent == null) ? this : parent.GetRoot();
        }

        internal Guid? GetItemId()
        {
            return itemId;
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
            target.Write(new String(' ', indentation));
            target.Write($"{ItemType} {name}");

            children.ForEach(child => child.Print(target, indentation + 2));
        }
    }
}
