# Formbase.Mcp

A read-only [MCP](https://modelcontextprotocol.io) door for a Formbase host.

The host hosts it: its access key and namespace selection come first, then three tools answer from the same read paths
the REST API uses — `describe_form_type`, `query_records` and `projection_status`. There are no tools that write, and the
port the tools stand on (`IFormbaseReadPort`) has no member that could.

It is off unless `Formbase:Mcp:Enabled` is `true`, and the host refuses to start with it on while no access key is
configured (`Formbase:Access:Keys`): an open host is a deployment choice, an open door that agents call by themselves is
not one to make by omission.

**An access key opens the whole host.** Formbase knows no individual callers, so whoever holds a key an MCP client is
given can read everything the key reads. Hand an agent a key meant for it, and rotate it like any other.

```csharp
builder.Services.AddFormbaseMcp(builder.Configuration, accessKeysRequired: …);
builder.Services.AddScoped<IFormbaseReadPort, YourReadPort>();
app.MapFormbaseMcp();   // maps nothing when the door is off
```
