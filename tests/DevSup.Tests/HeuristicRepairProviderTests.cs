using DevSup.Agent.Repair;
using DevSup.Core;
using DevSup.Core.Models;

namespace DevSup.Tests;

public sealed class HeuristicRepairProviderTests : IDisposable
{
    private readonly string _root;

    public HeuristicRepairProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "devsup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // best-effort cleanup
        }
    }

    private static FailureEvent Failure(string path = "/api/orders") => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = Guid.NewGuid(),
        StatusCode = 500,
        Method = "GET",
        Path = path,
        ExceptionMessage = "NullReferenceException: Object reference not set to an instance of an object.",
        StackTrace = "at Handlers.OrderHandler.Get() in src/Handlers/OrderHandler.cs:line 12",
        OccurredAt = DateTimeOffset.UtcNow
    };

    private static void WriteRepairsJson(string dir, string json)
    {
        var target = Path.Combine(dir, ".devsup");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "repairs.json"), json);
    }

    private static string OrderHandlerSource() => """
        public static class OrderHandler
        {
            public static IResult Get(int orderId, IOrderRepository repository)
            {
                var order = repository.GetOrder(orderId);
                return Results.Ok(order);
            }
        }
        """;

    private const string TemplateWithOrderRepair = """
        {
          "repairs": [
            {
              "path": "/api/orders",
              "kind": "NullReference",
              "file": "src/Handlers/OrderHandler.cs",
              "fragment": "var order = repository.GetOrder(orderId);",
              "replacement": "var order = repository.GetOrder(orderId);\nif (order is null)\n{\n    return Results.NotFound(new { error = \"order not found\" });\n}",
              "summary": "Guard missing order lookups against null"
            }
          ]
        }
        """;

    [Fact]
    public void Repair_WhenPathMatches_AppliesSingleVerifiedReplacement()
    {
        WriteRepairsJson(_root, TemplateWithOrderRepair);
        var handlerDir = Path.Combine(_root, "src", "Handlers");
        Directory.CreateDirectory(handlerDir);
        File.WriteAllText(Path.Combine(handlerDir, "OrderHandler.cs"), OrderHandlerSource());

        var proposal = new HeuristicRepairProvider().Repair(Failure(), ErrorKind.NullReference, _root);

        Assert.True(proposal.HasPatch);
        Assert.Equal("src/Handlers/OrderHandler.cs", proposal.RelativeFilePath);
        Assert.Contains("return Results.NotFound(new { error = \"order not found\" });", proposal.RepairedContent);
        Assert.Equal("Guard missing order lookups against null", proposal.Summary);
    }

    [Fact]
    public void Repair_WhenOnlyKindMatches_AppliesPatch()
    {
        WriteRepairsJson(_root, TemplateWithOrderRepair);
        var handlerDir = Path.Combine(_root, "src", "Handlers");
        Directory.CreateDirectory(handlerDir);
        File.WriteAllText(Path.Combine(handlerDir, "OrderHandler.cs"), OrderHandlerSource());

        var proposal = new HeuristicRepairProvider().Repair(Failure("/api/anything"), ErrorKind.NullReference, _root);

        Assert.True(proposal.HasPatch);
    }

    [Fact]
    public void Repair_NoTemplateFile_EscalatesToHuman()
    {
        var proposal = new HeuristicRepairProvider().Repair(Failure(), ErrorKind.NullReference, _root);

        Assert.False(proposal.HasPatch);
        Assert.Contains("No .devsup/repairs.json", proposal.Analysis);
    }

    [Fact]
    public void Repair_MissingTargetFile_EscalatesToHuman()
    {
        WriteRepairsJson(_root, """{"repairs":[{"path":"/api/orders","file":"src/Handlers/OrderHandler.cs","fragment":"var order = repository.GetOrder(orderId);","replacement":"guard"}]}""");

        var proposal = new HeuristicRepairProvider().Repair(Failure(), ErrorKind.NullReference, _root);

        Assert.False(proposal.HasPatch);
        Assert.Contains("does not exist", proposal.Analysis);
    }

    [Fact]
    public void Repair_FragmentNotFound_EscalatesToHuman()
    {
        WriteRepairsJson(_root, TemplateWithOrderRepair);
        var handlerDir = Path.Combine(_root, "src", "Handlers");
        Directory.CreateDirectory(handlerDir);
        File.WriteAllText(Path.Combine(handlerDir, "OrderHandler.cs"), "public static class OrderHandler { } // no fragment");

        var proposal = new HeuristicRepairProvider().Repair(Failure(), ErrorKind.NullReference, _root);

        Assert.False(proposal.HasPatch);
        Assert.Contains("was not found", proposal.Analysis);
    }

    [Fact]
    public void Repair_AmbiguousFragment_EscalatesToHuman()
    {
        WriteRepairsJson(_root, TemplateWithOrderRepair);
        var handlerDir = Path.Combine(_root, "src", "Handlers");
        Directory.CreateDirectory(handlerDir);
        var duplicated = OrderHandlerSource() + "\n\n" + OrderHandlerSource();
        File.WriteAllText(Path.Combine(handlerDir, "OrderHandler.cs"), duplicated);

        var proposal = new HeuristicRepairProvider().Repair(Failure(), ErrorKind.NullReference, _root);

        Assert.False(proposal.HasPatch);
        Assert.Contains("ambiguous", proposal.Analysis);
    }

    [Fact]
    public void Repair_PathTraversalOutsideWorkspace_Rejected()
    {
        WriteRepairsJson(_root, """{"repairs":[{"path":"/api/orders","file":"../outside.txt","fragment":"boom","replacement":"guard"}]}""");

        var proposal = new HeuristicRepairProvider().Repair(Failure(), ErrorKind.NullReference, _root);

        Assert.False(proposal.HasPatch);
        Assert.Contains("outside the repository", proposal.Analysis);
    }

    [Fact]
    public void Repair_InvalidJson_EscalatesToHuman()
    {
        WriteRepairsJson(_root, "{ not json !!");

        var proposal = new HeuristicRepairProvider().Repair(Failure(), ErrorKind.NullReference, _root);

        Assert.False(proposal.HasPatch);
        Assert.Contains("could not be parsed", proposal.Analysis);
    }
}