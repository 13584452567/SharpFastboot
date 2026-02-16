using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpFastboot.DataModel
{
    public class FastbootResponse
    {
        public FastbootState Result { get; set; }
        public string Response { get; set; } = "";
        public byte[]? Data { get; set; }
        public int Size { get; set; }
        public List<string> Info { get; set; } = new List<string>();
        public string Text { get; set; } = "";

        public FastbootResponse ThrowIfError()
        {
            if (Result == FastbootState.Fail)
                throw new Exception("Error: remote: " + Enum.GetName(Result) + "\n" +
                    $"({Response})");
            return this;
        }
    }

    public enum FastbootState
    {
        Success,
        Fail,
        Text,
        Data,
        Info,
        Unknown,
        Timeout
    }
}
