using Kokos.LanguageServer;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Server;

var server = await LanguageServer.From(options => options
    .WithInput(Console.OpenStandardInput())
    .WithOutput(Console.OpenStandardOutput())
    .WithServices(services => services.AddSingleton<KokosDocumentStore>())
    .WithHandler<KokosTextDocumentHandler>()
    .WithHandler<KokosHoverHandler>());

await server.WaitForExit;
