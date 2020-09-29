using ReportPortal.Client.Abstractions.Models;
using ReportPortal.Client.Abstractions.Requests;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace RanorexOrangebeardListener.Requests
{
    /// <summary>
    /// Defines a request to update specified launch.
    /// </summary>
    [DataContract]
    [KnownType(typeof(UpdateOrangebeardLaunchRequest))]
    public class UpdateOrangebeardLaunchRequest : UpdateLaunchRequest
    {
        /// <summary>
        /// Update attributes for launch.
        /// </summary>
        [DataMember(Name = "attributes", EmitDefaultValue = false)]
        public List<ItemAttribute> Attributes { get; set; }
    }

}
