﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Exceltk.Util {
    /// <summary>
    /// Command Parser
    /// </summary>
    public class CommandParser {
        // Variables
        private readonly Dictionary<string, string> Parameters;

        // Constructor
        public CommandParser(IEnumerable<string> Args) {
            Parameters=new Dictionary<string, string>();
            var Spliter=new Regex(@"^-{1,2}",
                                    RegexOptions.IgnoreCase|RegexOptions.Compiled);

            var Remover=new Regex(@"^['""]?(.*?)['""]?$",
                                    RegexOptions.IgnoreCase|RegexOptions.Compiled);

            string Parameter=null;

            // Valid parameters forms:
            // {-,/,--}param value(",'))
            // Examples: 
            // -param1 value1 -param2 value2" 
            foreach (string Txt in Args){
                // Look for new parameters (-,/ or --) and a
                // possible enclosed value (=,:)
                string[] Parts = Spliter.Split(Txt, 3);

                switch (Parts.Length) {
                    // Found a value (for the last parameter 
                    // found (space separator))
                    case 1:
                        if (Parameter!=null) {
                            if (!Parameters.ContainsKey(Parameter)) {
                                Parts[0]=Remover.Replace(Parts[0], "$1");
                                Parameters.Add(Parameter, Parts[0]);
                            }
                            Parameter=null;
                        }
                        // else Error: no parameter waiting for a value (skipped)
                        break;

                    // Found just a parameter
                    case 2:
                        // The last parameter is still waiting. 
                        // With no value, set it to true.
                        if (Parameter!=null) {
                            if (!Parameters.ContainsKey(Parameter)) {
                                Parameters.Add(Parameter, "true");
                            }
                        }
                        Parameter=Parts[1];
                        break;

                    // Parameter with enclosed value
                    case 3:
                        // The last parameter is still waiting. 
                        // With no value, set it to true.
                        if (Parameter!=null) {
                            if (!Parameters.ContainsKey(Parameter)) {
                                Parameters.Add(Parameter, "true");
                            }
                        }

                        Parameter=Parts[1];

                        // Remove possible enclosing characters (",')
                        if (!Parameters.ContainsKey(Parameter)) {
                            Parts[2]=Remover.Replace(Parts[2], "$1");
                            Parameters.Add(Parameter, Parts[2]);
                        }

                        Parameter=null;
                        break;
                }
            }
            // In case a parameter is still waiting
            if (Parameter!=null) {
                if (!Parameters.ContainsKey(Parameter))
                    Parameters.Add(Parameter, "true");
            }
        }

        // Retrieve a parameter value if it exists 
        // (overriding C# indexer property)
        public string this[string Param] {
            get {
                if (Parameters.ContainsKey(Param)) {
                    return Parameters[Param];
                }
                return null;
            }
        }

        public override string ToString() {
            var sb=new StringBuilder();
            foreach (var p in Parameters) {
                sb.AppendFormat("{0}:{1}" + Environment.NewLine, p.Key, p.Value);
            }

            return sb.ToString();
        }
    }
}