using System;

namespace Flow.Launcher.Plugin.UnityEngine;

class Project
{
    public string   Name         { get; set; }
    public string   Path         { get; set; }
    public string   Version      { get; set; }
    public bool     IsFavorite   { get; set; }
    public DateTime DateModified { get; set; }
}