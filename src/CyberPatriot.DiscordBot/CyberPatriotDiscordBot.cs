﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using LiteDB;
using CyberPatriot.DiscordBot.Services;

namespace CyberPatriot.DiscordBot
{
    internal class CyberPatriotDiscordBot
    {
        // I don't like big static properties
        public static DateTimeOffset StartupTime { get; private set; }
        
        static void Main(string[] args)
           => new CyberPatriotDiscordBot().MainAsync().GetAwaiter().GetResult();

        private DiscordSocketClient _client;
        private IConfiguration _config;

        public async Task MainAsync()
        {
            _client = new DiscordSocketClient();
            _config = BuildConfig();

            var services = ConfigureServices();
            services.GetRequiredService<LogService>();
            await Task.WhenAll(
                services.GetRequiredService<CommandHandlingService>().InitializeAsync(services),
                services.GetRequiredService<IDataPersistenceService>().InitializeAsync(services),
                services.GetRequiredService<CyberPatriotEventHandlingService>().InitializeAsync(services),
                services.GetRequiredService<IScoreRetrievalService>().InitializeAsync(services),
                services.GetRequiredService<ICompetitionRoundLogicService>().InitializeAsync(services)
            );

            string enableUpNotificationConfSetting = _config["enableUpNotification"] ?? "false";

            if (bool.TryParse(enableUpNotificationConfSetting, out bool enableUpNotification) && enableUpNotification)
            {
                _client.Ready += async () => await (await (await _client.GetApplicationInfoAsync())?.Owner?.GetOrCreateDMChannelAsync())?.SendMessageAsync($"[{DateTimeOffset.Now.ToString("g")}] Now online!");
            }

            _client.Ready += () =>
            {
                StartupTime = DateTimeOffset.UtcNow;
                return Task.CompletedTask;
            };

            await _client.LoginAsync(Discord.TokenType.Bot, _config["token"]);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private IServiceProvider ConfigureServices()
        {
            return new ServiceCollection()
                // Base
                .AddSingleton(_client)
                .AddSingleton<CommandService>()
                .AddSingleton<CommandHandlingService>()
                // Logging
                .AddLogging()
                .AddSingleton<LogService>()
                // Extra
                .AddSingleton(_config)
                .AddSingleton<IDataPersistenceService, LiteDbDataPersistenceService>(prov => new LiteDbDataPersistenceService(new LiteDatabase(_config["databaseFilename"])))
                .AddSingleton<PreferenceProviderService>()
                // CyPat
                // Scoreboard trial order: live, JSON archive, CSV released archive
                .AddSingleton<IScoreRetrievalService, FallbackScoreRetrievalService>(prov => new FallbackScoreRetrievalService(
                    prov,
                    async innerProv => { 
                        var httpServ = new HttpScoreboardScoreRetrievalService();
                        await httpServ.InitializeAsync(innerProv);
                        return httpServ;
                    },
                    async innerProv =>
                        // if the constructor throws an exception, e.g. missing config, means this provider is skipped
                        new JsonScoreRetrievalService(await System.IO.File.ReadAllTextAsync(innerProv.GetRequiredService<IConfiguration>()["jsonSource"])),
                    async innerProv => await new SpreadsheetScoreRetrievalService().InitializeFromConfiguredCsvAsync(innerProv)
                ))
                .AddSingleton<ICompetitionRoundLogicService, CyberPatriotTenCompetitionRoundLogicService>()
                .AddSingleton<FlagProviderService>()
                .AddSingleton<CyberPatriotEventHandlingService>()
                .AddSingleton<ScoreboardMessageBuilderService>()
                .BuildServiceProvider();
        }

        private IConfiguration BuildConfig()
        {
            return new ConfigurationBuilder()
                .SetBasePath(System.IO.Directory.GetCurrentDirectory())
                .AddJsonFile("config.json")
                .Build();
        }
    }
}
