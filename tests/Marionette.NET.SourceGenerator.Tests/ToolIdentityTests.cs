// Marionette.NET — Phase 2.2 ToolIdentity unit tests
//
// Hosted in the SourceGenerator.Tests project because that project already
// has a ProjectReference on Marionette.NET.Runtime (so we can construct
// CallableDescriptor instances and call ToolIdentity directly). No
// separate Runtime.Tests project exists yet; Phase 6 will add one.
//
// Coverage:
//   * Default tool name is "<rootName>.<methodName>"; method casing
//     preserved.
//   * Description-only changes leave the hash UNCHANGED (idempotence;
//     MASTERPLAN Spielregel 5).
//   * Signature changes (add a parameter, rename a parameter, change a
//     parameter type) DO change the hash.
//   * Two callables with the same name + different signatures get
//     disambiguated with a stable 8-hex suffix.

using System;
using System.Collections.Generic;

using Marionette.Runtime.Manifest;
using Marionette.Runtime.Tools;

using Xunit;

namespace Marionette.SourceGenerator.Tests;

public class ToolIdentityTests
{
    private static CallableDescriptor MakeCallable(
        string name,
        string description,
        params (string Name, string Type)[] parameters)
    {
        var paramList = new List<ParamDescriptor>();
        foreach (var p in parameters)
        {
            paramList.Add(new ParamDescriptor(
                Name: p.Name, ClrTypeName: p.Type, IsRequired: true, DefaultValue: null));
        }
        return new CallableDescriptor(
            Name: name,
            Description: description,
            OffUiThread: false,
            TimeoutSeconds: 0,
            IsAsync: false,
            Parameters: paramList,
            ParametersJsonSchema: "{\"type\":\"object\"}",
            Invoke: (instance, args) => null);
    }

    [Fact]
    public void ComputeToolName_PreservesRootAndMethodCasing()
    {
        var c = MakeCallable("AddTodo", "Adds a todo.", ("title", "string"));
        var name = ToolIdentity.ComputeToolName("TodoListViewModel", c);
        Assert.Equal("TodoListViewModel.AddTodo", name);
    }

    [Fact]
    public void ComputeStableHash_IsDeterministic()
    {
        var c = MakeCallable("AddTodo", "Adds a todo.", ("title", "string"));
        var h1 = ToolIdentity.ComputeStableHash("TodoListViewModel", c);
        var h2 = ToolIdentity.ComputeStableHash("TodoListViewModel", c);
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // SHA-256 hex
    }

    [Fact]
    public void ComputeStableHash_IgnoresDescriptionChange()
    {
        // MASTERPLAN Spielregel 5: idempotente Tool-Identity. Description-only
        // changes must NOT alter the hash so Claude's tool cache doesn't
        // churn.
        var c1 = MakeCallable("AddTodo", "Adds a todo.", ("title", "string"));
        var c2 = MakeCallable("AddTodo", "Adds a todo to the list, appending it at the end.", ("title", "string"));
        Assert.Equal(
            ToolIdentity.ComputeStableHash("TodoListViewModel", c1),
            ToolIdentity.ComputeStableHash("TodoListViewModel", c2));
    }

    [Fact]
    public void ComputeStableHash_ChangesOnSignatureChange()
    {
        var sig1 = MakeCallable("AddTodo", "x", ("title", "string"));
        var sigAddedParam = MakeCallable("AddTodo", "x", ("title", "string"), ("priority", "int"));
        var sigRenamedParam = MakeCallable("AddTodo", "x", ("name", "string"));
        var sigChangedType = MakeCallable("AddTodo", "x", ("title", "int"));

        var baseHash = ToolIdentity.ComputeStableHash("TodoListViewModel", sig1);
        Assert.NotEqual(baseHash, ToolIdentity.ComputeStableHash("TodoListViewModel", sigAddedParam));
        Assert.NotEqual(baseHash, ToolIdentity.ComputeStableHash("TodoListViewModel", sigRenamedParam));
        Assert.NotEqual(baseHash, ToolIdentity.ComputeStableHash("TodoListViewModel", sigChangedType));
    }

    [Fact]
    public void DisambiguateOverloads_LeavesUniqueNamesAlone()
    {
        var input = new List<(string Name, string Hash)>
        {
            ("Root.MethodA", "aaaaaaaa00000000"),
            ("Root.MethodB", "bbbbbbbb00000000"),
            ("Root.MethodC", "cccccccc00000000"),
        };
        var output = ToolIdentity.DisambiguateOverloads(input);
        Assert.Equal(3, output.Count);
        Assert.Equal("Root.MethodA", output[0].Name);
        Assert.Equal("Root.MethodB", output[1].Name);
        Assert.Equal("Root.MethodC", output[2].Name);
    }

    [Fact]
    public void DisambiguateOverloads_AppendsHexSuffixOnCollision()
    {
        // Same base name (Root.Add) twice — second should get a 8-hex suffix.
        var input = new List<(string Name, string Hash)>
        {
            ("Root.Add", "deadbeef99999999"),
            ("Root.Add", "cafef00d77777777"),
        };
        var output = ToolIdentity.DisambiguateOverloads(input);
        Assert.Equal(2, output.Count);
        Assert.Equal("Root.Add", output[0].Name);
        Assert.Equal("Root.Add_cafef00d", output[1].Name);
    }

    [Fact]
    public void DisambiguateOverloads_StableAcrossRuns()
    {
        // The suffix is derived from the hash, so the same input must always
        // produce the same disambiguated names.
        var input = new List<(string Name, string Hash)>
        {
            ("Root.Method", "1234567890abcdef"),
            ("Root.Method", "fedcba0987654321"),
        };
        var run1 = ToolIdentity.DisambiguateOverloads(input);
        var run2 = ToolIdentity.DisambiguateOverloads(input);
        for (int i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].Name, run2[i].Name);
        }
    }

    [Fact]
    public void NormalizeClrTypeName_StripsGlobalPrefix()
    {
        Assert.Equal("System.Int32", ToolIdentity.NormalizeClrTypeName("global::System.Int32"));
        Assert.Equal("int", ToolIdentity.NormalizeClrTypeName("int"));
    }

    [Fact]
    public void BuildCanonicalSignature_NewlineFormat()
    {
        // Format spec from the helper:
        //   <rootName>\n<methodName>\n<param>:<type>\n...
        var c = MakeCallable("AddTodo", "x", ("title", "string"), ("priority", "int"));
        var canonical = ToolIdentity.BuildCanonicalSignature("TodoListViewModel", c);
        Assert.Equal("TodoListViewModel\nAddTodo\ntitle:string\npriority:int", canonical);
    }

    [Fact]
    public void BuildCanonicalSignature_ZeroParameters_NoTrailingNewline()
    {
        var c = MakeCallable("Reset", "x");
        var canonical = ToolIdentity.BuildCanonicalSignature("Calculator", c);
        Assert.Equal("Calculator\nReset", canonical);
    }
}
