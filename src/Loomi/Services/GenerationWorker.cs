using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
public class GenerationWorker(IServiceScopeFactory scopes, BrowserAutomationService browser, IImageStorage storage, IHubContext<StatusHub> hub, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Generations.Where(g => g.Status != RunStatus.Queued && g.Status != RunStatus.Completed && g.Status != RunStatus.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, RunStatus.Failed).SetProperty(g => g.ErrorMessage, "Interrupted"), stoppingToken);
            foreach (var project in await db.Projects.ToListAsync(stoppingToken))
                project.Status = await db.Generations.AnyAsync(g => g.ProjectId == project.Id && g.Status == RunStatus.Queued, stoppingToken) ? "Queued" : "Ready";
            await db.SaveChangesAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var g = await db.Generations.Include(g => g.Project).Where(g => g.Status == RunStatus.Queued).OrderBy(g => g.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                if (g == null) { await Task.Delay(1000, stoppingToken); continue; }
                async Task Report(RunStatus status)
                {
                    g.Status = status; g.Project.Status = status.ToString(); g.Project.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                    try { await hub.Clients.All.SendAsync("GenerationUpdated", GenerationDto.From(g), stoppingToken); } catch (OperationCanceledException) { throw; } catch { /* polling repairs missed notifications */ }
                }
                try
                {
                    Generation? parent = g.ParentGenerationId == null ? null : await db.Generations.FindAsync([g.ParentGenerationId.Value], stoppingToken);
                    var result = await browser.RunAsync(g, parent?.LocalImagePath is { } path ? storage.Resolve(path) : null, parent?.ConversationUrl, Report, stoppingToken);
                    g.LocalImagePath = await storage.SaveAsync(g.ProjectId, g.Id, result.Image, stoppingToken);
                    g.ConversationUrl = result.ConversationUrl; g.Project.ConversationUrl = result.ConversationUrl;
                    await Report(RunStatus.Completed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Do not log browser exception text: it can contain URLs, page content or tokens.
                    logger.LogWarning("Generation {GenerationId} failed ({Type})", g.Id, ex.GetType().Name);
                    var allowed = new[] { "LoginRequired", "GenerationTimeout", "InvalidImage", "ConversationNotSaved" };
                    g.ErrorMessage = allowed.Contains(ex.Message) ? ex.Message : "AutomationFailed";
                    await Report(RunStatus.Failed);
                }
                g.Project.Status = await db.Generations.AnyAsync(x => x.ProjectId == g.ProjectId && x.Status == RunStatus.Queued, stoppingToken) ? "Queued" : "Ready";
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Queue iteration failed ({Type})", ex.GetType().Name); await Task.Delay(3000, stoppingToken); }
        }
    }
}
