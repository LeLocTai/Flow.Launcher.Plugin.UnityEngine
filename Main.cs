using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Flow.Launcher.Plugin.UnityEngine
{
public class UnityEngine : IAsyncPlugin, IAsyncReloadable, IContextMenu
{
    private PluginInitContext context = null!;

    string? hubExePath;

    readonly List<Project>              projects = new();
    readonly Dictionary<string, Editor> editors  = new();

    public async Task InitAsync(PluginInitContext _context)
    {
        context = _context;

        await ReloadDataAsync();
    }

    public async Task ReloadDataAsync()
    {
        projects.Clear();
        editors.Clear();

        hubExePath = Path.Join((string?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Unity Technologies\Hub",
                                                          "InstallLocation", null) ?? "",
                               "Unity Hub.exe");

        if (!File.Exists(hubExePath))
        {
            context.API.ShowMsg($"Unity Hub not found at {hubExePath}");
            return;
        }

        var hubDataPath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub");

        var favoriteProjectsJson = System.Text.RegularExpressions.Regex
                                         .Unescape(File.ReadAllText(Path.Join(hubDataPath, "favoriteProjects.json")))
                                         .Trim('\"');
        var favoriteProjects = JsonDocument.Parse(favoriteProjectsJson)
                                           .RootElement
                                           .EnumerateArray()
                                           .Select(e => e.GetString()!)
                                           .ToHashSet();


        var editorsLocations = new List<string> {
            @"C:\Program Files\Unity\Hub\Editor\",
        };

        var secondaryInstallPathFilePath = Path.Join(hubDataPath, "secondaryInstallPath.json");
        if (File.Exists(secondaryInstallPathFilePath))
        {
            editorsLocations.Add(System.Text.RegularExpressions.Regex
                                       .Unescape(File.ReadAllText(secondaryInstallPathFilePath))
                                       .Trim('\"'));
        }

        foreach (var editorsLocation in editorsLocations)
        {
            foreach (var editorLocation in Directory.GetDirectories(editorsLocation))
            {
                var version = Path.GetFileName(editorLocation);
                var exePath = Path.Join(editorLocation, "Editor", "Unity.exe");
                if (!File.Exists(exePath))
                    continue;

                editors.Add(version,
                            new Editor {
                                Path    = exePath,
                                Version = version,
                            });
            }
        }

        var projectsPath = JsonDocument.Parse(File.ReadAllText(Path.Join(hubDataPath, "projectDir.json")))
                                       .RootElement
                                       .GetProperty("directoryPath")
                                       .GetString();

        var projectPaths = new HashSet<string>();

        if (projectsPath != null)
        {
            projectPaths.UnionWith(Directory.GetDirectories(projectsPath));
        }

        var unityRegKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Unity Technologies\Unity Editor 5.x");
        if (unityRegKey != null)
        {
            projectPaths.UnionWith(unityRegKey.GetValueNames()
                                              .Where(v => v.StartsWith("RecentlyUsedProjectPaths-"))
                                              .Select(v => (byte[])unityRegKey.GetValue(v)!)
                                              .Select(b => Encoding.UTF8.GetString(b))
                                              .Select(p => p.Replace(@"/", @"\")));
        }
        else
        {
            context.API.ShowMsg(@"SOFTWARE\Unity Technologies\Unity Editor 5.x not found");
        }

        var maybeProjects = await Task.WhenAll(projectPaths.Select(p => ProjectFromPath(p, favoriteProjects)));

        projects.AddRange(maybeProjects.OfType<Project>());
    }

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            var results = projects.Select(p => new Result {
                ContextData = p,
                Title       = p.Name,
                SubTitle = $"{(p.IsFavorite ? "★ " : "  ")}" +
                           $"{(p.IsVersionExists ? " " : "❌")}" +
                           $"{p.Version,-12}\t" +
                           $"{p.Path}",
                IcoPath = "Images/unity.png",
                Action  = _ => OnResultAction(p),
            });

            if (query.Search != "")
            {
                results = results.Select(r =>
                {
                    var project = (Project)r.ContextData;
                    var match   = context.API.FuzzySearch(query.Search, project.Name);
                    r.Score = match.Score;
                    return r;
                }).Where(r => r.Score > 0);
            }

            results = results.Select(r =>
            {
                var project = (Project)r.ContextData;

                if (project.IsFavorite)
                    r.Score += 100;

                r.Score += Math.Max(0, 50 - (DateTime.Now - project.DateModified).Days * 3);

                return r;
            });

            return results.ToList();
        }, token);
    }

    bool OnResultAction(Project project)
    {
        var exePath = hubExePath;

        if (editors.TryGetValue(project.Version, out var editor))
        {
            exePath = editor.Path;
        }
        else
        {
            context.API.ShowMsg($"Unity version {project.Version} not found");
        }

        if (exePath == null)
        {
            context.API.ShowMsg($"Unity version {project.Version} and Unity Hub not found");
            return true;
        }

        var processStartInfo = new ProcessStartInfo {
            FileName        = exePath,
            UseShellExecute = true,
            ArgumentList = {
                "-projectPath",
                project.Path
            }
        };

        try
        {
            Task.Run(() =>
            {
                try
                {
                    Process.Start(processStartInfo);
                }
                catch (Exception e)
                {
                    context.API.ShowMsgError(e.Message, "Could not start " + exePath);
                }
            });
        }
        catch (Exception e)
        {
            context.API.ShowMsgError(e.Message, "Could not start " + exePath);
        }

        return true;
    }

    async Task<Project?> ProjectFromPath(string path, HashSet<string> favoriteProjects)
    {
        var projectVersionFilePath = Path.Join(path, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(projectVersionFilePath))
            return null;

        var versionDataText = await File.ReadAllTextAsync(projectVersionFilePath);
        var versionData     = versionDataText.Split('\n').Select(l => l.Split(": "));
        var version         = versionData.First(d => d[0] == "m_EditorVersion")[1].Trim();

        return new Project {
            Name            = Path.GetFileName(path),
            Path            = path,
            Version         = version,
            IsVersionExists = editors.ContainsKey(version),
            DateModified    = File.GetLastWriteTime(path),
            IsFavorite      = favoriteProjects.Contains(path)
        };
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        return new List<Result>();
    }
}
}
