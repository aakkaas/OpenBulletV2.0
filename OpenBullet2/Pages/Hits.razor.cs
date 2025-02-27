﻿using Blazored.Modal;
using Blazored.Modal.Services;
using GridBlazor;
using GridBlazor.Pages;
using GridMvc.Server;
using GridShared;
using GridShared.Utility;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using OpenBullet2.Auth;
using OpenBullet2.Entities;
using OpenBullet2.Helpers;
using OpenBullet2.Models.Data;
using OpenBullet2.Models.Hits;
using OpenBullet2.Models.Jobs;
using OpenBullet2.Repositories;
using OpenBullet2.Services;
using OpenBullet2.Shared.Forms;
using RuriLib.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenBullet2.Pages
{
    public partial class Hits
    {
        [Inject] private IModalService Modal { get; set; }
        [Inject] private IHitRepository HitRepo { get; set; }
        [Inject] private IJobRepository JobRepo { get; set; }
        [Inject] private IGuestRepository GuestRepo { get; set; }
        [Inject] private JobManagerService JobManager { get; set; }
        [Inject] private JobFactoryService JobFactory { get; set; }
        [Inject] private ConfigService ConfigService { get; set; }
        [Inject] private AuthenticationStateProvider Auth { get; set; }
        [Inject] private PersistentSettingsService PersistentSettings { get; set; }
        [Inject] private VolatileSettingsService VolatileSettings { get; set; }
        [Inject] private RuriLibSettingsService RuriLibSettings { get; set; }
        [Inject] private NavigationManager Nav { get; set; }

        private List<HitEntity> hits = new();
        private HitEntity selectedHit;
        private int uid = -1;

        private GridComponent<HitEntity> gridComponent;
        private CGrid<HitEntity> grid;
        private Task gridLoad;

        protected override async Task OnParametersSetAsync()
        {
            uid = await ((OBAuthenticationStateProvider)Auth).GetCurrentUserId();

            hits = uid == 0
                ? await HitRepo.GetAll().ToListAsync()
                : await HitRepo.GetAll().Where(h => h.OwnerId == uid).ToListAsync();

            Action<IGridColumnCollection<HitEntity>> columns = c =>
            {
                c.Add(h => h.Data).Titled(Loc["Data"]);
                c.Add(h => h.Type).Titled(Loc["Type"]);
                c.Add(h => h.ConfigName).Titled(Loc["Config"]);
                c.Add(h => h.Date).Titled(Loc["Date"]);
                c.Add(h => h.WordlistName).Titled(Loc["Wordlist"]);
                c.Add(h => h.Proxy).Titled(Loc["Proxy"]);
                c.Add(h => h.CapturedData).Titled(Loc["CapturedData"]);
            };

            var query = new QueryDictionary<StringValues>();
            query.Add("grid-page", "1");

            var client = new GridClient<HitEntity>(q => GetGridRows(columns, q), query, false, "hitsGrid", columns, CultureInfo.CurrentCulture)
                .Sortable()
                .Filterable()
                .WithMultipleFilters()
                .SetKeyboard(true)
                .ChangePageSize(true)
                .WithGridItemsCount()
                .ExtSortable()
                .Selectable(true, false, true);
            grid = client.Grid;

            // Try to set a previous filter
            if (VolatileSettings.GridQueries.ContainsKey((uid, "hitsGrid")))
            {
                grid.Query = VolatileSettings.GridQueries[(uid, "hitsGrid")];
            }

            // Set new items to grid
            gridLoad = client.UpdateGrid();
            await gridLoad;
        }

        private ItemsDTO<HitEntity> GetGridRows(Action<IGridColumnCollection<HitEntity>> columns,
                QueryDictionary<StringValues> query)
        {
            VolatileSettings.GridQueries[(uid, "hitsGrid")] = query;

            var server = new GridServer<HitEntity>(hits, new QueryCollection(query),
                true, "hitsGrid", columns, 15).Sortable().Filterable().WithMultipleFilters();

            // Return items to displays
            return server.ItemsToDisplay;
        }

        protected void OnHitSelected(object item)
        {
            if (item.GetType() == typeof(HitEntity))
            {
                selectedHit = (HitEntity)item;
            }
        }

        private async Task RefreshList()
        {
            hits = uid == 0 
                ? await HitRepo.GetAll().ToListAsync()
                : await HitRepo.GetAll().Where(h => h.OwnerId == uid).ToListAsync();

            await gridComponent.UpdateGrid();
            StateHasChanged();
        }

        private async Task EditHit()
        {
            if (selectedHit == null)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            var parameters = new ModalParameters();
            parameters.Add(nameof(HitEdit.Hit), selectedHit);

            var modal = Modal.Show<HitEdit>(Loc["EditHit"], parameters);
            await modal.Result;

            await RefreshList();
        }

        private async Task DeleteHit()
        {
            var selected = grid.SelectedItems.Cast<HitEntity>().ToList();

            if (selected.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            if (await js.Confirm(Loc["AreYouSure"], $"{Loc["ReallyDelete"]} {selected.Count} {Loc["hits"]}?", Loc["Cancel"]))
            {
                // Delete the hit from the db
                await HitRepo.Delete(selected);

                // Delete the hit from the local list
                selected.ForEach(h => hits.Remove(h));
            }

            await RefreshList();
        }

        private async Task DeleteFiltered()
        {
            var selected = grid.Items.ToList();

            if (selected.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            if (await js.Confirm(Loc["AreYouSure"], $"{Loc["ReallyDelete"]} {selected.Count} {Loc["hits"]}?", Loc["Cancel"]))
            {
                // Delete the hit from the db
                await HitRepo.Delete(selected);

                // Delete the hit from the local list
                selected.ForEach(h => hits.Remove(h));
            }

            await RefreshList();
        }

        private async Task DeleteDuplicates()
        {
            var duplicates = hits
                .GroupBy(h => h.GetHashCode(PersistentSettings.OpenBulletSettings.GeneralSettings.IgnoreWordlistNameOnHitsDedupe))
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.OrderBy(h => h.Date)
                .Reverse().Skip(1)).ToList();

            if (await js.Confirm(Loc["AreYouSure"], $"{Loc["ReallyDelete"]} {duplicates.Count} {Loc["hits"]}?", Loc["Cancel"]))
            {
                // Delete the hit from the db
                await HitRepo.Delete(duplicates);

                // Delete the hit from the local list
                duplicates.ForEach(h => hits.Remove(h));
            }

            await RefreshList();
        }

        private async Task PurgeHits()
        {
            if (await js.Confirm(Loc["AreYouSure"], Loc["ReallyDeleteAllHits"], Loc["Cancel"]))
            {
                if (uid == 0)
                {
                    HitRepo.Purge();
                }
                else
                {
                    await HitRepo.Delete(hits);
                }
                
                await RefreshList();
            }
        }

        private async Task SendToRecheck()
        {
            var selected = grid.SelectedItems.Cast<HitEntity>().ToList();

            if (selected.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            // If the hits refer to multiple configs, error
            if (selected.GroupBy(h => h.ConfigId).Count() > 1)
            {
                await js.AlertError(Loc["Uh-Oh"], Loc["HitsFromMultipleConfigsSelected"]);
                return;
            }

            var jobOptions = new MultiRunJobOptions();
            var wordlistType = RuriLibSettings.Environment.WordlistTypes.First().Name;

            // Get the config
            var config = ConfigService.Configs.FirstOrDefault(c => c.Id == selected.First().ConfigId);

            // If we cannot find a config with that id anymore, don't set it
            if (config == null)
            {
                await js.AlertError(Loc["Uh-Oh"], $"{(Loc["ConfigMissing"])} {selected.First().ConfigId} ({selected.First().ConfigName})");
            }

            jobOptions.ConfigId = config.Id;
            jobOptions.Bots = config.Settings.GeneralSettings.SuggestedBots;
            wordlistType = config.Settings.DataSettings.AllowedWordlistTypes.First();

            // Write the temporary file
            var tempFile = Path.GetTempFileName();
            File.WriteAllLines(tempFile, selected.Select(h => h.Data));
            var dataPoolOptions = new FileDataPoolOptions
            {
                FileName = tempFile,
                WordlistType = wordlistType
            };
            jobOptions.DataPool = dataPoolOptions;
            jobOptions.HitOutputs.Add(new DatabaseHitOutputOptions());

            // Create the job entity and add it to the database
            var jsonSettings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };
            var jobOptionsWrapper = new JobOptionsWrapper { Options = jobOptions };

            var entity = new JobEntity
            {
                Owner = await GuestRepo.Get(uid),
                CreationDate = DateTime.Now,
                JobType = JobType.MultiRun,
                JobOptions = JsonConvert.SerializeObject(jobOptionsWrapper, jsonSettings)
            };

            await JobRepo.Add(entity);

            try
            {
                var job = JobFactory.FromOptions(entity.Id, entity.Owner == null ? 0 : entity.Owner.Id, jobOptions);

                JobManager.Jobs.Add(job);
                Nav.NavigateTo($"jobs/edit/{job.Id}");
            }
            catch (Exception ex)
            {
                await js.AlertException(ex);
            }
        }

        private async Task CopyHitDataCapture()
        {
            var selectedHits = grid.SelectedItems.Cast<HitEntity>().ToList();

            if (selectedHits.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            var sb = new StringBuilder();
            selectedHits.ForEach(i => sb.AppendLine($"{i.Data} | {i.CapturedData}"));

            try
            {
                await js.CopyToClipboard(sb.ToString());
            }
            catch
            {
                await js.AlertError(Loc["CopyToClipboardFailed"], Loc["CopyToClipboardFailedMessage"]);
            }
        }

        private async Task CopyHitData()
        {
            var selectedHits = grid.SelectedItems.Cast<HitEntity>().ToList();

            if (selectedHits.Count == 0)
            {
                await ShowNoHitSelectedWarning();
                return;
            }

            var sb = new StringBuilder();
            selectedHits.ForEach(i => sb.AppendLine(i.Data));

            try
            {
                await js.CopyToClipboard(sb.ToString());
            }
            catch
            {
                await js.AlertError(Loc["CopyToClipboardFailed"], Loc["CopyToClipboardFailedMessage"]);
            }
        }

        private async Task ShowNoHitSelectedWarning()
            => await js.AlertError(Loc["Uh-Oh"], Loc["NoHitSelectedWarning"]);
    }
}
