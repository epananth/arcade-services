// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Autofac;
using EntityFrameworkCore.Triggers;
using FluentValidation.AspNetCore;
using Maestro.AzureDevOps;
using Maestro.Contracts;
using Maestro.Data;
using Maestro.Data.Models;
using Maestro.GitHub;
using Maestro.MergePolicies;
using Microsoft.AspNetCore.ApiPagination;
using Microsoft.AspNetCore.ApiVersioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.ServiceFabric.ServiceHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using IHostingEnvironment = Microsoft.AspNetCore.Hosting.IHostingEnvironment;

namespace Maestro.Web
{
    public partial class Startup
    {
        static Startup()
        {
            Triggers<BuildChannel>.Inserted += entry =>
            {
                DbContext context = entry.Context;
                BuildChannel entity = entry.Entity;

                StartAssociatedReleasePipelinesAsync(entry).GetAwaiter().GetResult();

                var queue = context.GetService<BackgroundQueue>();
                var dependencyUpdater = context.GetService<IDependencyUpdater>();
                queue.Post(() => dependencyUpdater.StartUpdateDependenciesAsync(entity.BuildId, entity.ChannelId));
            };
        }

        private static async Task StartAssociatedReleasePipelinesAsync(IInsertedEntry<BuildChannel, DbContext> entry)
        {
            BuildAssetRegistryContext context = entry.Context as BuildAssetRegistryContext;
            ILogger<BuildChannel> logger = context.GetService<ILogger<BuildChannel>>();
            BuildChannel entity = entry.Entity;
            Build barBuildInfo = entity.Build;
            Channel channel = context.Channels
                .Where(ch => ch.Id == entity.ChannelId)
                .Include(ch => ch.ChannelReleasePipelines)
                .ThenInclude(crp => crp.ReleasePipeline)
                .First();

            // If something use the old API version we won't have this information available.
            // This will also be the case if something adds an existing build (created using 
            // the old API version) to a channel
            if (barBuildInfo.AzureDevOpsBuildId == null)
            {
                logger.LogWarning($"barBuildInfo.AzureDevOpsBuildId is null for BAR Build.Id {barBuildInfo.Id}.");
                return;
            }

            if (channel.ChannelReleasePipelines?.Any() != true)
            {
                logger.LogWarning($"Channel {channel.Id}, which build with BAR ID {barBuildInfo.Id} is attached to, doesn't have an associated publishing pipeline.");
                return;
            }

            var azdoTokenProvider = context.GetService<IAzureDevOpsTokenProvider>();
            var accessToken = azdoTokenProvider.GetTokenForAccount(barBuildInfo.AzureDevOpsAccount).GetAwaiter().GetResult();

            var azdoClient = new AzureDevOpsClient(accessToken, logger, null);

            var azdoBuild = await azdoClient.GetBuildAsync(
                barBuildInfo.AzureDevOpsAccount,
                barBuildInfo.AzureDevOpsProject,
                barBuildInfo.AzureDevOpsBuildId.Value);

            foreach (var pipeline in channel.ChannelReleasePipelines)
            {
                try
                {
                    var account = pipeline.ReleasePipeline.Organization;
                    var project = pipeline.ReleasePipeline.Project;
                    var pipelineId = pipeline.ReleasePipeline.PipelineIdentifier;

                    var pipeDef = await azdoClient.GetReleaseDefinitionAsync(account, project, pipelineId);

                    pipeDef = await azdoClient.RemoveAllArtifactSourcesAsync(account, project, pipeDef);

                    pipeDef = await azdoClient.AddArtifactSourceAsync(account, project, pipeDef, azdoBuild);

                    var releaseId = await azdoClient.StartNewReleaseAsync(account, project, pipeDef, barBuildInfo.Id);

                    azdoClient.WaitUntilCompleted(account, project, releaseId);
                }
                catch (HttpRequestException e)
                {
                    logger.LogError($"Some problem happened interacting with DevOps API: {e.Message}", e);
                }
                catch (JsonException e)
                {
                    logger.LogError($"Some problem happened serializing/deserializaing JSON data: {e.Message}", e);
                }
                catch (Exception e)
                {
                    logger.LogError($"Some problem happened while starting publishing pipeline " +
                        $"{pipeline.ReleasePipeline.PipelineIdentifier} for build " +
                        $"{barBuildInfo.AzureDevOpsBuildId}: {e.Message}", e);
                }
            }
        }

        public Startup(IHostingEnvironment env)
        {
            HostingEnvironment = env;
            Configuration = ServiceHostConfiguration.Get(env);
        }

        public IHostingEnvironment HostingEnvironment { get; set; }
        public IConfigurationRoot Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            if (HostingEnvironment.IsDevelopment())
            {
                services.AddDataProtection();
            }
            else
            {
                IConfigurationSection dpConfig = Configuration.GetSection("DataProtection");

                string vaultUri = Configuration["KeyVaultUri"];
                string keyVaultKeyIdentifierName = dpConfig["KeyIdentifier"];
                KeyVaultClient kvClient = ServiceHostConfiguration.GetKeyVaultClient(HostingEnvironment);
                KeyBundle key = kvClient.GetKeyAsync(vaultUri, keyVaultKeyIdentifierName).GetAwaiter().GetResult();
                services.AddDataProtection()
                    .PersistKeysToAzureBlobStorage(new Uri(dpConfig["KeyFileUri"]))
                    .ProtectKeysWithAzureKeyVault(kvClient, key.KeyIdentifier.ToString());
            }

            ConfigureApiServices(services);

            services.Configure<CookiePolicyOptions>(
                options =>
                {
                    options.CheckConsentNeeded = context => true;
                    options.MinimumSameSitePolicy = SameSiteMode.None;
                });

            services.AddDbContext<BuildAssetRegistryContext>(
                options =>
                {
                    options.UseSqlServer(Configuration.GetSection("BuildAssetRegistry")["ConnectionString"]);
                });

            services.AddMvc()
                .SetCompatibilityVersion(CompatibilityVersion.Version_2_1)
                .AddFluentValidation(options => options.RegisterValidatorsFromAssemblyContaining<Startup>())
                .AddRazorPagesOptions(
                    options =>
                    {
                        options.Conventions.AuthorizeFolder("/");
                        options.Conventions.AllowAnonymousToPage("/Index");
                        options.Conventions.AllowAnonymousToPage("/SwaggerUi");
                    })
                .AddGitHubWebHooks()
                .AddApiPagination()
                .AddCookieTempDataProvider(
                    options =>
                    {
                        // Cookie Policy will not send this cookie unless we mark it as Essential
                        // The application will not function without this cookie.
                        options.Cookie.IsEssential = true;
                    });

            services.AddSingleton<IConfiguration>(Configuration);

            ConfigureAuthServices(services);

            services.AddSingleton<BackgroundQueue>();
            services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BackgroundQueue>());

            services.AddServiceFabricService<IDependencyUpdater>("fabric:/MaestroApplication/DependencyUpdater");

            services.AddGitHubTokenProvider();
            services.Configure<GitHubTokenProviderOptions>(
                (options, provider) =>
                {
                    IConfigurationSection section = Configuration.GetSection("GitHub");
                    section.Bind(options);
                    options.ApplicationName = "Maestro";
                    options.ApplicationVersion = Assembly.GetEntryAssembly()
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion;
                });
            services.AddAzureDevOpsTokenProvider();
            services.Configure<AzureDevOpsTokenProviderOptions>(
                (options, provider) =>
                {
                    var config = provider.GetRequiredService<IConfiguration>();
                    var tokenMap = config.GetSection("AzureDevOps:Tokens").GetChildren();
                    foreach (IConfigurationSection token in tokenMap)
                    {
                        options.Tokens.Add(token.GetValue<string>("Account"), token.GetValue<string>("Token"));
                    }
                });

            services.AddMergePolicies();
        }

        public void ConfigureContainer(ContainerBuilder builder)
        {
            builder.AddServiceFabricActor<ISubscriptionActor>();
            builder.AddServiceFabricActor<IPullRequestActor>();
        }

        private void ConfigureApiExceptions(IApplicationBuilder app)
        {
            app.Run(
                async ctx =>
                {
                    var result = new ApiError("An error occured.");
                    MvcJsonOptions jsonOptions =
                        ctx.RequestServices.GetRequiredService<IOptions<MvcJsonOptions>>().Value;
                    string output = JsonConvert.SerializeObject(result, jsonOptions.SerializerSettings);
                    ctx.Response.StatusCode = (int) HttpStatusCode.InternalServerError;
                    await ctx.Response.WriteAsync(output, Encoding.UTF8);
                });
        }

        private void ConfigureApi(IApplicationBuilder app)
        {
            app.UseExceptionHandler(ConfigureApiExceptions);

            app.UseAuthentication();
            app.UseMvc();

            app.Use(
                (ctx, next) =>
                {
                    if (ctx.Request.Path == "/api/swagger.json")
                    {
                        var vcp = ctx.RequestServices.GetRequiredService<VersionedControllerProvider>();
                        string highestVersion = vcp.Versions.Keys.OrderByDescending(n => n).First();
                        ctx.Request.Path = $"/api/{highestVersion}/swagger.json";
                    }

                    return next();
                });

            app.UseSwagger(
                options =>
                {
                    options.RouteTemplate = "api/{documentName}/swagger.json";
                    options.PreSerializeFilters.Add(
                        (doc, req) =>
                        {
                            doc.Host = req.Host.Value;
                            if (HostingEnvironment.IsDevelopment() && !Program.RunningInServiceFabric())
                            {
                                doc.Schemes = new List<string> {"http"};
                            }
                            else
                            {
                                doc.Schemes = new List<string> {"https"};
                            }

                            req.HttpContext.Response.Headers["Access-Control-Allow-Origin"] = "*";
                        });
                });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
                app.UseHttpsRedirection();
            }

            app.MapWhen(ctx => ctx.Request.Path.StartsWithSegments("/api"), ConfigureApi);

            app.UseRewriter(new RewriteOptions().AddRedirect("^swagger(/ui)?/?$", "/swagger/ui/index.html"));
            app.UseStatusCodePages();
            app.UseCookiePolicy();
            app.UseStaticFiles();
            app.UseAuthentication();
            app.UseMvc();
        }
    }
}
