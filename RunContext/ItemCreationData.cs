using Orangebeard.Client.V3.Entity;
using System;
using System.Collections.Generic;

namespace RanorexOrangebeardListener.RunContext
{
    public class ItemCreationData
    {
        public DateTime StartTime;
        public string Type;
        public string Name;
        public string Description;
        public ISet<Orangebeard.Client.V3.Entity.Attribute> Attributes;
    }
}
