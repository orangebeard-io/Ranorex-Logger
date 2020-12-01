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
 * Reference the Orangebeard.Client DLL
 * Build the Ranorex Logger DLL

## Install

 * Add your dll as a reference in your Ranorex Solution
 * Reference it in Program.cs `using RanorexOrangebeardListener;`
 * Attach the logger to your Ranorex report (environment vars can of course be set up elsewhere):
```cs
    Environment.SetEnvironmentVariable("orangebeard.endpoint", "https://your-instance.orangebeard.app");
    Environment.SetEnvironmentVariable("orangebeard.accessToken", "api-token-for-orangebeard");
    Environment.SetEnvironmentVariable("orangebeard.project", "projectname");
    Environment.SetEnvironmentVariable("orangebeard.testrun", "Test Run name");

    OrangebeardLogger orangebeard = new OrangebeardLogger();
    Report.AttachLogger(orangebeard);
```

Now run your test as you normally do and see the results fly in to Orangebeard!

