﻿/*
 * Copyright 2020 Orangebeard.io (https://www.orangebeard.io)
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;

namespace RanorexOrangebeardListener
{
    [Serializable]
    internal class MissingEnvironmentVariableException : Exception
    {
        public MissingEnvironmentVariableException()
        {

        }

        public MissingEnvironmentVariableException(string varName)
            : base($"Missing Configuration. No variable named: {varName}")
        {
        }

    }
}
