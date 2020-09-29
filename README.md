<h1 align="center">
  <a href="https://github.com/orangebeard-io/Ranorex-Logger">
    <img src="https://raw.githubusercontent.com/orangebeard-io/Ranorex-Logger/master/.github/logo.svg" alt="Orangebeard.io FitNesse TestSystemListener" height="200">
  </a>
  <br>Orangebeard.io Ranorex Report Logger<br>
</h1>

<h4 align="center">A Report Logger to report Ranorex tests in Orangebeard.</h4>

<p align="center">
  <a href="https://github.com/orangebeard-io/Ranorex-Logger/blob/master/LICENSE.txt">
    <img src="https://img.shields.io/github/license/orangebeard-io/Ranorex-Logger?style=flat-square"
      alt="License" />
  </a>
</p>

<div align="center">
  <h4>
    <a href="https://orangebeard.io">Orangebeard</a> |
    <a href="#build">Build</a> |
    <a href="#install">Install</a>
  </h4>
</div>

## Build
 * Clone this repository
 * Open in a .Net IDE
 * Add ReportPortal.Client and ReportPortal.Shared dependencies from NuGet (both 3.0.0 version)
 * Build the DLL

## Install

 * Add your dll as a reference in your Ranorex Solution
 * Reference it in Program.cs `using RanorexOrangebeardListener;`
 * Add ReportPortal.Client and ReportPortal.Shared dependencies from NuGet (both 3.0.0 version) to your project - Yes, this Logger will also work with ReportPortal!
 * Attach the logger to your Ranorex report (environment vars can of course be set up elsewhere):
```cs
    Environment.SetEnvironmentVariable("orangebeard.endpoint", "https://your-instance.orangebeard.app");
    Environment.SetEnvironmentVariable("orangebeard.token", "api-token-for-orangebeard");
    Environment.SetEnvironmentVariable("orangebeard.project", "projectname");
    Environment.SetEnvironmentVariable("orangebeard.testrun", "Test Run name);

    OrangebeardLogger orangebeard = new OrangebeardLogger();
    Report.AttachLogger(orangebeard);
```

Now run your test as you normally do and see the results fly in to Orangebeard!
 
## Limitations
 - Set Up and teardown steps are only marked as such when they are inside a test case or smart folder
 - No display of module groups (the underlying modules are reported), as Ranorex does not expose them in a way we can corellate between group and current log info
