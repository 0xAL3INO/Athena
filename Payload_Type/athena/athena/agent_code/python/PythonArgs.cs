using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace python
{
    public class PythonArgs
    {
        public string action { get; set; } //Run, Load, Reset
        public string arguments { get; set; } = string.Empty;
        public string script { get; set; } = string.Empty; //This can either be a script or a zip file containing a library if action is set to load
        public string file { get; set; }

        public bool Validate(out string message)
        {
            message = string.Empty;

            List<string> allowed_vals = new List<string> { "load", "run", "reset" };

            if (string.IsNullOrEmpty(this.action) || !allowed_vals.Contains(this.action.ToLower()))
            {
                message = "Unsupported action: " + this.action;
                return false;
            }

            if (this.action.ToLower() == "load" && string.IsNullOrEmpty(file))
            {
                message = "No file provided.";
                return false;
            }

            if(this.action.ToLower() == "run" && string.IsNullOrEmpty(script))
            {
                message = "No script provided.";
                return false;
            }

            return true;
        }
    }
}
