using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PluginDebugger.Runtime;

namespace SmokeTest
{
    /// <summary>
    /// Drives <see cref="PluginRunner"/> directly (no XrmToolBox host, no live connection) to
    /// validate the keystone: child-domain load + run, trace/SDK capture, symbol detection, and
    /// — critically — that the original plugin dll is left UNLOCKED so a rebuild can happen
    /// while the harness is "open".
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            var pluginDll = args.Length > 0
                ? args[0]
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    @"..\..\..\..\samples\SamplePlugin\bin\Debug\SamplePlugin.dll"));

            Console.WriteLine("Plugin under test: " + pluginDll);
            if (!File.Exists(pluginDll))
            {
                Console.WriteLine("FAIL: sample plugin not found. Build SamplePlugin first.");
                return 1;
            }

            CheckFormShapeMatrix();
            CheckTypedAttributeJson();
            CheckSharedVariables();
            CheckInputParameters();
            CheckExecutionContextImport();
            CheckHydration();
            CheckVsMonikerParser();

            var log = new RunLogSink();
            var logLines = new List<string>();
            log.EntryLogged += (s, e) => { Console.WriteLine($"   [{e.Category,-8}] {e.Message}"); logLines.Add(e.Message); };

            // 1. Type enumeration must not lock the dll and must find the plugin.
            Console.WriteLine("\n== ListPluginTypes ==");
            var types = PluginRunner.ListPluginTypes(pluginDll);
            foreach (var t in types)
            {
                Console.WriteLine($"   {t.FullName}  (configCtor={t.HasConfigCtor})");
            }
            Check("found GreetingPlugin", types.Any(t => t.FullName == "SamplePlugin.GreetingPlugin"));
            var greeting = types.First(t => t.FullName == "SamplePlugin.GreetingPlugin");

            // 2. Run in Full-mock so no live service is needed; the Create call should be intercepted.
            Console.WriteLine("\n== Run (FullMock) ==");
            var request = new RunRequest
            {
                PluginTypeName = greeting.FullName,
                UnsecureConfig = "hello-config",
                SecureConfig = null,
                Mode = ExecutionMode.FullMock,
                Context = new ContextDto
                {
                    MessageName = "Create",
                    PrimaryEntityName = "account",
                    Stage = 20,
                    Mode = 1,
                    Depth = 3,
                    UserId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    TargetKind = TargetKind.Entity,
                    TargetXml = SdkXml.Serialize(new Entity("account") { ["name"] = "Contoso Ltd" }, typeof(Entity))
                }
            };
            request.Context.SharedVariables.Add(new SharedVariableDto
            {
                Key = "note",
                ValueType = "String",
                ValueXml = SdkXml.Serialize("hello-sv", typeof(object))
            });
            request.Context.InputParameters.Add(new InputParameterDto
            {
                Key = "reason",
                ValueType = "String",
                ValueXml = SdkXml.Serialize("smoke-test", typeof(object))
            });

            var outcome = PluginRunner.Run(pluginDll, request, new StubService(), log);

            Check("run succeeded", outcome.Result.Success);
            Check("symbols loaded (.pdb present)", outcome.SymbolsLoaded);
            Check("context depth/mode/userId crossed the boundary",
                logLines.Any(l => l.Contains("depth=3") && l.Contains("mode=1") && l.Contains("33333333-3333-3333-3333-333333333333")));
            Check("typed SharedVariable crossed the boundary",
                logLines.Any(l => l.Contains("note=hello-sv")));
            Check("arbitrary InputParameter crossed the boundary",
                logLines.Any(l => l.Contains("reason=smoke-test")));
            Check("OutputParameters[greeting] captured",
                outcome.Result.OutputParameters.Any(p => p.Key == "greeting" && p.Display.Contains("Contoso")));

            // 3. Custom workflow activity (§4.12): discover it, bind an input argument, run it.
            Console.WriteLine("\n== Workflow activity (§4.12) ==");
            var activity = types.FirstOrDefault(t => t.FullName == "SamplePlugin.GreetingActivity");
            Check("GreetingActivity discovered as a workflow activity",
                activity != null && activity.Kind == PluginTypeKind.WorkflowActivity);
            if (activity != null)
            {
                Check("reflected required input argument 'Name'",
                    activity.Arguments.Any(a => a.Name == "Name" && a.IsInput && a.Required && a.TypeName == "String"));
                Check("reflected output argument 'Greeting'",
                    activity.Arguments.Any(a => a.Name == "Greeting" && a.IsOutput && a.TypeName == "String"));

                var wfRequest = new RunRequest
                {
                    PluginTypeName = activity.FullName,
                    Kind = PluginTypeKind.WorkflowActivity,
                    Mode = ExecutionMode.FullMock,
                    Context = new ContextDto { MessageName = "Create", PrimaryEntityName = "account", StageName = "PostOperation", WorkflowMode = 1 },
                    InputArguments =
                    {
                        new WorkflowArgumentDto { Name = "Name", ValueType = "String", ValueXml = SdkXml.Serialize("Temmy", typeof(object)) },
                        new WorkflowArgumentDto { Name = "Times", ValueType = "WholeNumber", ValueXml = SdkXml.Serialize(3, typeof(object)) }
                    }
                };

                var wfOutcome = PluginRunner.Run(pluginDll, wfRequest, new StubService(), log);
                Check("workflow run succeeded", wfOutcome.Result.Success);
                Check("input argument reached the activity", logLines.Any(l => l.Contains("Name='Temmy'") && l.Contains("Times=3")));
                Check("output argument 'Greeting' captured",
                    wfOutcome.Result.OutputParameters.Any(p => p.Key == "Greeting" && p.Display.Contains("Temmy")));
            }

            // 4. The original dll must be writable now (proves it was never locked by the run).
            Console.WriteLine("\n== File-lock check ==");
            Check("original dll is unlocked (rebuild possible)", CanOpenForWrite(pluginDll));

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED ✓" : $"{_failures} CHECK(S) FAILED ✗");
            return _failures == 0 ? 0 : 1;
        }

        private static void CheckFormShapeMatrix()
        {
            Console.WriteLine("\n== FormShape matrix (§4.3) ==");

            // message, stage, targetEditor, pre, post, outputId, changedAttrsOnly
            (string msg, int stage, TargetEditorKind tgt, bool pre, bool post, bool outId, bool changed)[] expected =
            {
                ("Create", 10, TargetEditorKind.EntityAttributes, false, false, false, false),
                ("Create", 20, TargetEditorKind.EntityAttributes, false, false, false, false),
                ("Create", 40, TargetEditorKind.EntityAttributes, false, true,  true,  false),
                ("Update", 10, TargetEditorKind.EntityAttributes, true,  false, false, true),
                ("Update", 20, TargetEditorKind.EntityAttributes, true,  false, false, true),
                ("Update", 40, TargetEditorKind.EntityAttributes, true,  true,  false, true),
                ("Delete", 10, TargetEditorKind.EntityReference,  true,  false, false, false),
                ("Delete", 20, TargetEditorKind.EntityReference,  true,  false, false, false),
                ("Delete", 40, TargetEditorKind.EntityReference,  true,  false, false, false),
            };

            foreach (var e in expected)
            {
                var shape = FormShapeEngine.Resolve(e.msg, e.stage);
                bool ok = shape.TargetEditor == e.tgt
                          && shape.PreImageAllowed == e.pre
                          && shape.PostImageAllowed == e.post
                          && shape.ExposesOutputId == e.outId
                          && shape.TargetIsChangedAttributesOnly == e.changed;
                Check($"{e.msg}/{e.stage}: target={shape.TargetEditor} pre={shape.PreImageAllowed} post={shape.PostImageAllowed} outId={shape.ExposesOutputId} changed={shape.TargetIsChangedAttributesOnly}", ok);
            }

            bool threw = false;
            try { FormShapeEngine.Resolve("Associate", 20); }
            catch (ArgumentException) { threw = true; }
            Check("unsupported message (Associate) rejected", threw);
        }

        private static void CheckTypedAttributeJson()
        {
            Console.WriteLine("\n== Typed attribute mapper + JSON envelope (§4.5) ==");

            Check("Integer -> WholeNumber (unambiguous)",
                AttributeTypeMapper.FromTypeCode(AttributeTypeCode.Integer) == AttributeEditorKind.WholeNumber
                && !AttributeTypeMapper.IsAmbiguous(AttributeEditorKind.WholeNumber));
            Check("Picklist -> OptionSet (ambiguous)",
                AttributeTypeMapper.FromTypeCode(AttributeTypeCode.Picklist) == AttributeEditorKind.OptionSet
                && AttributeTypeMapper.IsAmbiguous(AttributeEditorKind.OptionSet));
            Check("Customer -> Lookup",
                AttributeTypeMapper.FromTypeCode(AttributeTypeCode.Customer) == AttributeEditorKind.Lookup);
            Check("Money -> Money (ambiguous)",
                AttributeTypeMapper.FromTypeCode(AttributeTypeCode.Money) == AttributeEditorKind.Money
                && AttributeTypeMapper.IsAmbiguous(AttributeEditorKind.Money));

            // Round-trip a representative spread of kinds.
            var original = new[]
            {
                new TypedAttribute("name", AttributeEditorKind.String, "Contoso"),
                new TypedAttribute("creditonhold", AttributeEditorKind.Boolean, true),
                new TypedAttribute("numberofemployees", AttributeEditorKind.WholeNumber, 50),
                new TypedAttribute("revenue", AttributeEditorKind.Money, 1234.56m),
                new TypedAttribute("statuscode", AttributeEditorKind.OptionSet, 2),
                new TypedAttribute("new_choices", AttributeEditorKind.MultiSelectOptionSet, new System.Collections.Generic.List<int> { 1, 3, 5 }),
                new TypedAttribute("primarycontactid", AttributeEditorKind.Lookup, Guid.Parse("11111111-1111-1111-1111-111111111111"), "contact"),
                new TypedAttribute("new_when", AttributeEditorKind.DateTime, new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc)),
            };

            var json = AttributeJson.Export(original);
            Console.WriteLine(json);

            var kinds = original.ToDictionary(a => a.LogicalName, a => a.Kind);
            var imported = AttributeJson.Import(json, n => kinds.TryGetValue(n, out var k) ? k : (AttributeEditorKind?)null);

            Check("round-trip import succeeded", imported.Success);
            Check("round-trip preserved all attributes", imported.Attributes.Count == original.Length);
            var lookup = imported.Attributes.FirstOrDefault(a => a.LogicalName == "primarycontactid");
            Check("lookup envelope kept entity+id", lookup != null && lookup.LookupEntity == "contact"
                && (Guid)lookup.Value == Guid.Parse("11111111-1111-1111-1111-111111111111"));
            var money = imported.Attributes.FirstOrDefault(a => a.LogicalName == "revenue");
            Check("money round-trips to Money SDK value", money != null
                && money.ToSdkValue() is Microsoft.Xrm.Sdk.Money m && m.Value == 1234.56m);

            // Plain value on an ambiguous column must be rejected, not guessed.
            var bad = AttributeJson.Import("{\"statuscode\": 2}", n => n == "statuscode" ? AttributeEditorKind.OptionSet : (AttributeEditorKind?)null);
            Check("plain value on ambiguous column rejected", !bad.Success && bad.Errors.Count == 1);

            // Plain unambiguous values accepted.
            var good = AttributeJson.Import("{\"name\":\"x\",\"creditonhold\":false,\"numberofemployees\":7}",
                n => n == "name" ? AttributeEditorKind.String : n == "creditonhold" ? AttributeEditorKind.Boolean : AttributeEditorKind.WholeNumber);
            Check("plain unambiguous values accepted", good.Success && good.Attributes.Count == 3);
        }

        private static void CheckSharedVariables()
        {
            Console.WriteLine("\n== SharedVariables typed values (§4.4) ==");

            // Parse then serialize-as-object and deserialize, mirroring the run pipeline.
            (SharedVariableType type, string text, object expected)[] cases =
            {
                (SharedVariableType.String, "hello", "hello"),
                (SharedVariableType.WholeNumber, "42", 42),
                (SharedVariableType.Boolean, "true", true),
                (SharedVariableType.Decimal, "12.5", 12.5m),
                (SharedVariableType.Guid, "22222222-2222-2222-2222-222222222222", Guid.Parse("22222222-2222-2222-2222-222222222222")),
            };

            foreach (var c in cases)
            {
                var boxed = SharedVariableValue.Parse(c.type, c.text);
                var xml = SdkXml.Serialize(boxed, typeof(object));
                var restored = SdkXml.Deserialize<object>(xml);
                Check($"{c.type} round-trips through object serialization", Equals(restored, c.expected));
            }
        }

        private static void CheckInputParameters()
        {
            Console.WriteLine("\n== Arbitrary InputParameters typed values (§4.6) ==");

            // Scalars round-trip like SharedVariables.
            var guid = Guid.Parse("44444444-4444-4444-4444-444444444444");
            (InputParameterType type, string text, object expected)[] scalars =
            {
                (InputParameterType.String, "hello", "hello"),
                (InputParameterType.WholeNumber, "7", 7),
                (InputParameterType.Boolean, "true", true),
                (InputParameterType.Guid, guid.ToString(), guid),
            };
            foreach (var c in scalars)
            {
                var restored = SdkXml.Deserialize<object>(SdkXml.Serialize(InputParameterValue.Parse(c.type, c.text), typeof(object)));
                Check($"{c.type} round-trips through object serialization", Equals(restored, c.expected));
            }

            // SDK types: Money / OptionSetValue / EntityReference keep their runtime type.
            var money = SdkXml.Deserialize<object>(SdkXml.Serialize(InputParameterValue.Parse(InputParameterType.Money, "19.95"), typeof(object)));
            Check("Money parses + round-trips", money is Money m && m.Value == 19.95m);

            var osv = SdkXml.Deserialize<object>(SdkXml.Serialize(InputParameterValue.Parse(InputParameterType.OptionSetValue, "3"), typeof(object)));
            Check("OptionSetValue parses + round-trips", osv is OptionSetValue o && o.Value == 3);

            var er = SdkXml.Deserialize<object>(SdkXml.Serialize(InputParameterValue.Parse(InputParameterType.EntityReference, "account:" + guid), typeof(object)));
            Check("EntityReference parses 'logical:guid' + round-trips",
                er is EntityReference r && r.LogicalName == "account" && r.Id == guid);

            bool threw = false;
            try { InputParameterValue.Parse(InputParameterType.EntityReference, "account"); }
            catch (FormatException) { threw = true; }
            Check("EntityReference without ':guid' rejected", threw);
        }

        private static void CheckExecutionContextImport()
        {
            Console.WriteLine("\n== Full execution-context import (§4.11) ==");

            var contextsDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\contexts"));

            // ----- Create with an Entity Target -----
            var createJson = File.ReadAllText(Path.Combine(contextsDir, "create-context.json"));
            var create = ExecutionContextImporter.Import(createJson);

            Check("create: message + stage", create.MessageName == "Create" && create.Stage == 40);
            Check("create: primary entity + id",
                create.PrimaryEntityName == "account" && create.PrimaryEntityId == Guid.Parse("5677734c-e7c9-eb11-bacc-000d3aa2b8f0"));
            Check("create: Target is an Entity", create.TargetKind == TargetKind.Entity && create.TargetEntity != null);
            Check("create: identity fields parsed",
                create.UserId == Guid.Parse("cbb18675-25b1-eb11-8236-000d3ac912e3")
                && create.BusinessUnitId == Guid.Parse("d8aa8675-25b1-eb11-8236-000d3ac912e3"));
            Check("create: OutputParameters[id] captured",
                create.OutputId == Guid.Parse("5677734c-e7c9-eb11-bacc-000d3aa2b8f0"));

            var target = create.TargetEntity;
            Check("create: string attribute", (target["name"] as string) == "Temmy");
            Check("create: OptionSetValue attribute", target["territorycode"] is OptionSetValue osv && osv.Value == 1);
            Check("create: EntityReference attribute",
                target["ownerid"] is EntityReference er && er.LogicalName == "systemuser"
                && er.Id == Guid.Parse("cbb18675-25b1-eb11-8236-000d3ac912e3"));
            Check("create: /Date()/ -> DateTime (UTC)",
                target["createdon"] is DateTime dt && dt.Kind == DateTimeKind.Utc && dt.Year == 2021);
            Check("create: bare guid string -> Guid", target["accountid"] is Guid);
            Check("create: null attribute skipped", !target.Contains("modifiedonbehalfby"));
            Check("create: FormattedValues not imported as attributes", !target.Contains("statecode") || target["statecode"] is OptionSetValue);
            Check("create: SharedVariables parsed (scalars)",
                create.SharedVariables.Count == 3 && create.SharedVariables.Any(s => s.Key == "IsAutoTransact" && (bool)s.Value));
            Check("create: no non-Target InputParameters at top level", create.InputParameters.Count == 0);
            Check("create: ParentContext ignored (no stray warnings)", create.Warnings.Count == 0);

            // ----- Delete with an EntityReference Target -----
            var deleteJson = File.ReadAllText(Path.Combine(contextsDir, "delete-context.json"));
            var del = ExecutionContextImporter.Import(deleteJson);

            Check("delete: message + stage", del.MessageName == "Delete" && del.Stage == 40);
            Check("delete: Target is an EntityReference",
                del.TargetKind == TargetKind.EntityReference && del.TargetReference != null
                && del.TargetReference.LogicalName == "account"
                && del.TargetReference.Id == Guid.Parse("be69f3f3-74c8-eb11-bacc-000d3aa2b8f0"));
            Check("delete: primary id from Target", del.PrimaryEntityId == Guid.Parse("be69f3f3-74c8-eb11-bacc-000d3aa2b8f0"));
            Check("delete: no stray warnings", del.Warnings.Count == 0);

            // ----- Robustness: unknown __type reported, not fatal -----
            var weird = ExecutionContextImporter.Import(
                "{\"MessageName\":\"Create\",\"Stage\":20,\"PrimaryEntityName\":\"account\"," +
                "\"InputParameters\":[{\"key\":\"widget\",\"value\":{\"__type\":\"Frobnicator:whatever\",\"X\":1}}]}");
            Check("unknown __type reported as a warning", weird.Warnings.Any(w => w.Contains("Frobnicator")));
            Check("unknown __type did not abort the import", weird.MessageName == "Create");

            bool threw = false;
            try { ExecutionContextImporter.Import("not json at all"); }
            catch (FormatException) { threw = true; }
            Check("malformed JSON throws FormatException", threw);
        }

        private static void CheckHydration()
        {
            Console.WriteLine("\n== Live-record hydration mapping (§4.2) ==");

            var record = new Entity("account", Guid.NewGuid())
            {
                ["name"] = "Contoso",
                ["creditonhold"] = true,
                ["numberofemployees"] = 50,
                ["revenue"] = new Money(9999.99m),
                ["statuscode"] = new OptionSetValue(1),
                ["primarycontactid"] = new EntityReference("contact", Guid.NewGuid()),
                ["createdon"] = DateTime.UtcNow,
                ["donotemail"] = (byte[])null // null is skipped
            };

            var typed = HydrationMapper.FromEntity(record);

            Check("hydration skips null attributes", typed.All(t => t.LogicalName != "donotemail"));
            Check("string mapped", typed.Any(t => t.LogicalName == "name" && t.Kind == AttributeEditorKind.String));
            Check("optionset mapped", typed.Any(t => t.LogicalName == "statuscode" && t.Kind == AttributeEditorKind.OptionSet && (int)t.Value == 1));
            Check("money mapped to decimal value", typed.Any(t => t.LogicalName == "revenue" && t.Kind == AttributeEditorKind.Money && (decimal)t.Value == 9999.99m));
            var lookup = typed.FirstOrDefault(t => t.LogicalName == "primarycontactid");
            Check("lookup keeps entity + id", lookup != null && lookup.Kind == AttributeEditorKind.Lookup && lookup.LookupEntity == "contact");

            // Hydrated values must rebuild into the same SDK objects.
            var rebuilt = TypedAttribute.ToEntity("account", typed);
            Check("rebuilt revenue is Money", rebuilt["revenue"] is Money mm && mm.Value == 9999.99m);
            Check("rebuilt statuscode is OptionSetValue", rebuilt["statuscode"] is OptionSetValue osv && osv.Value == 1);
            Check("rebuilt lookup is EntityReference", rebuilt["primarycontactid"] is EntityReference er && er.LogicalName == "contact");
        }

        private static void CheckVsMonikerParser()
        {
            Console.WriteLine("\n== Visual Studio moniker parsing (§4.9) ==");

            var ok2022 = VsMonikerParser.TryParse("!VisualStudio.DTE.17.0:12345", out var v2022, out var pid2022);
            Check("VS2022 moniker parsed", ok2022 && v2022 == "17.0" && pid2022 == 12345
                && VsMonikerParser.ProductName(v2022) == "Visual Studio 2022");

            var ok2019 = VsMonikerParser.TryParse("!VisualStudio.DTE.16.0:999", out var v2019, out var pid2019);
            Check("VS2019 moniker parsed", ok2019 && v2019 == "16.0" && pid2019 == 999
                && VsMonikerParser.ProductName(v2019) == "Visual Studio 2019");

            Check("non-DTE moniker rejected", !VsMonikerParser.TryParse("!SomeOther.Thing:1", out _, out _));
            Check("missing pid rejected", !VsMonikerParser.TryParse("!VisualStudio.DTE.17.0", out _, out _));
        }

        private static void Check(string name, bool condition)
        {
            Console.WriteLine($"   [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
            {
                _failures++;
            }
        }

        private static bool CanOpenForWrite(string path)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>A do-nothing service; in FullMock mode the wrapper never calls it.</summary>
        private sealed class StubService : IOrganizationService
        {
            public Guid Create(Entity entity) => throw new InvalidOperationException("StubService should not be called in FullMock mode.");
            public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new InvalidOperationException();
            public void Update(Entity entity) => throw new InvalidOperationException();
            public void Delete(string entityName, Guid id) => throw new InvalidOperationException();
            public OrganizationResponse Execute(OrganizationRequest request) => throw new InvalidOperationException();
            public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new InvalidOperationException();
            public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new InvalidOperationException();
            public EntityCollection RetrieveMultiple(QueryBase query) => throw new InvalidOperationException();
        }
    }
}
