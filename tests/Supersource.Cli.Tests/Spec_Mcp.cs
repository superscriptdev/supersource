using System;
using System.Collections.Generic;
using System.Text.Json;
using Supersource.Cli;
using Xunit;

namespace Supersource.Cli.Tests;

/// <summary>
/// The MCP shim: the protocol, and the argv a tool call becomes. `Answer` is exercised with a
/// recording dispatch, so these cases say what the shim does with a call without depending on
/// what any verb answers.
/// </summary>
public class Spec_Mcp
{
    /// <summary>A dispatch that records the argv it was handed and succeeds.</summary>
    sealed class Recorder
    {
        public List<string[]> Calls = new();
        public int Code;

        public int Run(string[] argv)
        {
            Calls.Add(argv);
            Console.Out.Write("recorded");
            return Code;
        }
    }

    static McpTool[] One(params McpParam[] takes)
    {
        return new[] { McpTool.Read("t.one", "a tool", new[] { "verb" }, takes) };
    }

    static string Call(McpTool[] tools, Recorder into, string arguments)
    {
        return Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":"
            + "{\"name\":\"t.one\",\"arguments\":" + arguments + "}}",
            tools, into.Run);
    }

    static JsonElement Result(string answer)
    {
        return JsonDocument.Parse(answer).RootElement.GetProperty("result");
    }

    // ---- the protocol -------------------------------------------------------------------

    [Fact]
    public void A_notification_is_answered_with_silence()
    {
        // No id, so no answer; `notifications/initialized` arrives on every connection.
        Assert.Null(Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            Array.Empty<McpTool>(), _ => 0));
    }

    [Fact]
    public void Malformed_input_and_missing_methods_are_protocol_errors()
    {
        Assert.Contains("-32700", Mcp.Answer("{not json", Array.Empty<McpTool>(), _ => 0));
        Assert.Contains("-32600", Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1}", Array.Empty<McpTool>(), _ => 0));
        Assert.Contains("-32601", Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"nope\"}", Array.Empty<McpTool>(), _ => 0));
        Assert.Contains("-32602", Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}",
            Array.Empty<McpTool>(), _ => 0));
    }

    [Fact]
    public void Initialize_and_ping_answer_without_a_tool_table()
    {
        string said = Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            Array.Empty<McpTool>(), _ => 0);
        Assert.Contains("protocolVersion", said);
        Assert.Contains("\"tools\":{}", said);

        Assert.Contains("\"result\":{}", Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}", Array.Empty<McpTool>(), _ => 0));
    }

    [Fact]
    public void A_schema_states_the_type_and_what_is_required()
    {
        // A switch is a boolean in the schema, and only the required parameters are listed as
        // required.
        McpTool[] tools = One(
            McpParam.At("file", "a path"),
            McpParam.At("text", "optional", false),
            McpParam.Switch("dry-run", "write nothing"));

        JsonElement schema = Result(Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", tools, _ => 0))
            .GetProperty("tools")[0].GetProperty("inputSchema");

        Assert.Equal("string", schema.GetProperty("properties").GetProperty("file").GetProperty("type").GetString());
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("dry-run").GetProperty("type").GetString());

        var required = new List<string>();
        foreach (JsonElement each in schema.GetProperty("required").EnumerateArray())
        {
            required.Add(each.GetString());
        }

        Assert.Equal(new[] { "file" }, required);
    }

    [Fact]
    public void A_write_says_so_in_its_description()
    {
        McpTool[] tools = { McpTool.Write("t.w", "Does a thing.", new[] { "v" }) };
        Assert.Contains("Writes a file.", Mcp.Answer(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", tools, _ => 0));
    }

    // ---- the argv ------------------------------------------------------------------------

    [Fact]
    public void Positionals_keep_their_declared_order()
    {
        var into = new Recorder();
        McpTool[] tools = One(McpParam.At("out", "first"), McpParam.At("program", "second"));

        // Named out of order on purpose: argv order comes from the table, not from the JSON.
        Call(tools, into, "{\"program\":\"p.json\",\"out\":\"o.ss\"}");

        Assert.Equal(new[] { "verb", "o.ss", "p.json" }, into.Calls[0]);
    }

    [Fact]
    public void Flags_follow_every_positional()
    {
        var into = new Recorder();
        McpTool[] tools = One(
            McpParam.At("file", "a path"),
            McpParam.Switch("dry-run", "write nothing"),
            McpParam.Option("expect-hash", "a digest"));

        Call(tools, into, "{\"file\":\"a.ss\",\"dry-run\":true,\"expect-hash\":\"abc\"}");

        // Flags follow the positionals: a verb reads its positionals by index, and some scan
        // the tail for options.
        Assert.Equal(new[] { "verb", "a.ss", "--dry-run", "--expect-hash", "abc" }, into.Calls[0]);
    }

    [Fact]
    public void A_flag_passed_false_is_left_out()
    {
        var into = new Recorder();
        McpTool[] tools = One(McpParam.At("file", "a path"), McpParam.Switch("check", "compare"));

        Call(tools, into, "{\"file\":\"a.ss\",\"check\":false}");

        // "check": false means do not check, so the flag is not appended.
        Assert.Equal(new[] { "verb", "a.ss" }, into.Calls[0]);
    }

    [Fact]
    public void An_absent_optional_is_skipped_and_an_absent_required_is_refused()
    {
        var into = new Recorder();
        McpTool[] tools = One(McpParam.At("file", "a path"), McpParam.At("text", "optional", false));

        Call(tools, into, "{\"file\":\"a.ss\"}");
        Assert.Equal(new[] { "verb", "a.ss" }, into.Calls[0]);

        JsonElement result = Result(Call(tools, into, "{\"text\":\"x\"}"));
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("missing required argument: file",
            result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Single(into.Calls);
    }

    [Fact]
    public void A_refusal_is_a_result_an_agent_can_read()
    {
        // A verb that exits nonzero has answered: the refusal is the result, carried under
        // `isError` rather than as a transport error.
        var into = new Recorder();
        into.Code = 3;

        JsonElement result = Result(Call(One(McpParam.At("file", "a path")), into, "{\"file\":\"a.ss\"}"));
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("recorded", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void What_a_verb_prints_does_not_reach_stdout()
    {
        // stdout is the JSON-RPC channel, so the shim redirects both streams for the call's
        // duration.
        var into = new Recorder();
        var captured = new System.IO.StringWriter();
        System.IO.TextWriter was = Console.Out;
        Console.SetOut(captured);
        try
        {
            Call(One(McpParam.At("file", "a path")), into, "{\"file\":\"a.ss\"}");
        }
        finally
        {
            Console.SetOut(was);
        }

        Assert.Equal("", captured.ToString());
    }

    // ---- the table itself ----------------------------------------------------------------

    [Fact]
    public void A_table_with_two_tools_of_one_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Mcp.Check(new[]
        {
            McpTool.Read("t.same", "one", new[] { "a" }),
            McpTool.Read("t.same", "two", new[] { "b" }),
        }));
    }

    [Fact]
    public void A_required_positional_after_an_optional_one_is_refused()
    {
        // argv would be off by a position whenever the optional one is absent.
        ArgumentException refused = Assert.Throws<ArgumentException>(() => Mcp.Check(One(
            McpParam.At("maybe", "optional", false),
            McpParam.At("must", "required"))));

        Assert.Contains("off by a position", refused.Message);
    }

    [Fact]
    public void The_formats_own_table_is_well_formed()
    {
        Mcp.Check(Mcp.Tools());
    }

    [Fact]
    public void Every_tool_in_the_formats_table_names_a_verb_this_layer_has()
    {
        // A tool whose verb nothing answers reports "unknown command" to its caller.
        var verbs = new HashSet<string>(Program.VerbNames, StringComparer.Ordinal);
        McpTool[] tools = Mcp.Tools();

        for (int i = 0; i < tools.Length; i++)
        {
            Assert.True(verbs.Contains(tools[i].Verb[0]),
                tools[i].Name + " names the verb \"" + tools[i].Verb[0] + "\", which this layer does not answer");
        }
    }
}
