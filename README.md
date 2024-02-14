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
(Preferred option is NuGet install!)
 * Clone this repository
 * Open in a .Net IDE
 * Reference Ranorex.Core.dll and Ranorex.Libs.Util.dll from your Ranorex Installation(s) - Ranorex 9.x uses net462, Ranorex 10.x uses net48. - Change the target framework(s) to what you need. Currently, the solution targets net462 and net48, so it needs Ranorex 9.x dll's, or separate references for v9 and v10.'
 * Reference the Orangebeard.Client DLL
 * Build the Ranorex Logger DLL

## Install
 * Install from NuGet
 * If you built it yourself: Add your dll as a reference in your Ranorex Solution
 * Reference it in Program.cs `using RanorexOrangebeardListener;`
 * Attach the logger to your Ranorex report (environment vars can of course be set up elsewhere):
```cs
    Environment.SetEnvironmentVariable("orangebeard.endpoint", "https://your-instance.orangebeard.app");
    Environment.SetEnvironmentVariable("orangebeard.accessToken", "api-token-for-orangebeard");
    Environment.SetEnvironmentVariable("orangebeard.project", "projectname");
    Environment.SetEnvironmentVariable("orangebeard.testrun", "Test Run name");
    Environment.SetEnvironmentVariable("orangebeard.description", @"test run description"); //OPTIONAL
    Environment.SetEnvironmentVariable("orangebeard.attributes", @"key:value;single tag"); //OPTIONAL
	Environment.SetEnvironmentVariable("orangebeard.ref.url", @"https://my-ci-server.net/PRJ/1234"); //OPTIONAL
    Environment.SetEnvironmentVariable("orangebeard.fileupload.patterns", @".*\.txt;.*\.bat"); //OPTIONAL

    OrangebeardLogger orangebeard = new OrangebeardLogger();
    Report.AttachLogger(orangebeard);
```

Now run your test as you normally do and see the results fly in to Orangebeard!

