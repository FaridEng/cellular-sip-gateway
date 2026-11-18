using DWM157Gateway.WinForms;

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(args.Contains("--start", StringComparer.OrdinalIgnoreCase)));
