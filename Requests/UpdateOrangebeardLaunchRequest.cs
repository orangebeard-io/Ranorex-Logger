/*
 * Copyright 2020 Orangebeard.io (https://www.orangebeard.io)
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

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
