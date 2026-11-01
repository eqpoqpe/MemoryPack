using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MemoryPack.Tests.Models;

[MemoryPackable]
public partial class CustomFormatterCheck
{
    public string? NoMarkField;
    public string? NoMarkProp { get; set; }

    [Utf8StringFormatter]
    public string? Field1;

    [Utf16StringFormatter]
    public string? Prop1 { get; set; }

    [OrdinalIgnoreCaseStringDictionaryFormatter<int>]
    public Dictionary<string, int>? PropDict { get; set; }
    [OrdinalIgnoreCaseStringDictionaryFormatter<string>]
    public Dictionary<string, string>? FieldDict;

}
