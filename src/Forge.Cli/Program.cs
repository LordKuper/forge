using System.CommandLine;
using Forge.Bootstrap;
using Forge.Cli;
using Forge.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using IHost host = ForgeHost.CreateBuilder().Build();
ILocalizationCatalog catalog = host.Services.GetRequiredService<ILocalizationCatalog>();
RootCommand root = CliApplication.CreateRootCommand(catalog, Console.Out);
return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
