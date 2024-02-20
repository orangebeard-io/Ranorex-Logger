using System;
using System.Collections.Generic;

namespace RanorexOrangebeardListener.RunContext
{
    public class ItemCreationData
    {
        private string _name;
        private string _description;

        public DateTime StartTime { get; set; }
        public string Type { get; set; }

        public string Name
        {
            get => string.IsNullOrEmpty(_name) ? "NO_NAME" : _name;
            set => _name = value;
        }

        public string Description
        {
            get => _description;
            set => _description = value?.Length > 1024 ? value.Substring(0, 1021) + "..." : value;
        }

        public ISet<Orangebeard.Client.V3.Entity.Attribute> Attributes { get; set; }
    }
    
}
