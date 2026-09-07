# Terminal pictogram appendix

CLI remains unchanged. These terminal glyphs are documented for completeness; proposed SVGs are for Desktop/Android GUI, not Spectre.Console replacements.

Source root: VPNRouter.CLI/Commands/.
- Error cross U+2717: StartCommand.cs 53,63,76,103,205,384,403,411,431,452; TestUpdateCommand.cs 59,66; ProfilesCommand.cs 125,183; ServiceCommand.cs 23,43,62,77,86,100,109,123,132.
- Success check U+2713: StartCommand.cs 160,318,388,395,418; ProfilesCommand.cs177; ServiceCommand.cs57,84,107,130; StatusCommand.cs111.
- Heavy success U+2714: StartCommand.cs446; DoctorCommand.cs34.
- Warning U+26A0: StartCommand.cs166,169,427; DoctorCommand.cs37.
- Heavy error U+2716: DoctorCommand.cs41.
- Event arrow U+2192: StartCommand.cs91,163. ServiceCommand.cs182 is a prose breadcrumb, not a pictogram.
- Spectre Spinner.Known.Dots: ProfilesCommand.cs30,164; frames owned by dependency, not embedded repository geometry.

Independent read-only source inventory found no additional user-facing pictograms in VPNRouter.GUI. No terminal behavior, color markup or characters have been changed. These source locations came from an independent read-only report and require no SVG integration.
