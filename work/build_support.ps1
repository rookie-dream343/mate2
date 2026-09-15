$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$managed=Join-Path $root 'app\MateEngineX_Data\Managed'
$rsp=Join-Path $PSScriptRoot 'support-refs.rsp'
$argsList=@('/nostdlib+','/target:library','/langversion:latest',('/out:"'+(Join-Path $managed 'SolaSupport.dll')+'"'))
$argsList+=Get-ChildItem -LiteralPath $managed -Filter '*.dll' | Where-Object Name -ne 'SolaSupport.dll' | ForEach-Object {'/reference:"'+$_.FullName+'"'}
$argsList+='"'+(Join-Path $PSScriptRoot 'SolaSupport.cs')+'"'
$argsList+='"'+(Join-Path $PSScriptRoot 'SolaDisplay.cs')+'"'
$argsList+='"'+(Join-Path $root 'voice\SolaVoice.cs')+'"'
$argsList | Set-Content -LiteralPath $rsp -Encoding utf8
& dotnet 'C:\Program Files\dotnet\sdk\10.0.300\Roslyn\bincore\csc.dll' ('@'+$rsp)
if($LASTEXITCODE -ne 0){throw 'Support compile failed'}
Add-Type -Path (Join-Path $root 'tools\mono.cecil\lib\net40\Mono.Cecil.dll')
$dll=Join-Path $managed 'Assembly-CSharp.dll'
$backup=Join-Path $PSScriptRoot 'Assembly-CSharp.original.dll'
if(!(Test-Path -LiteralPath $backup)){Copy-Item -LiteralPath $dll -Destination $backup}
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new();$resolver.AddSearchDirectory($managed)
$reader=[Mono.Cecil.ReaderParameters]::new();$reader.AssemblyResolver=$resolver
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($backup,$reader)
$support=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'SolaSupport.dll'),$reader)
$attach=($support.MainModule.Types | Where-Object Name -eq 'SolaSupport').Methods | Where-Object Name -eq 'Attach'
$awake=($assembly.MainModule.Types | Where-Object Name -eq 'SaveLoadHandler').Methods | Where-Object Name -eq 'Awake'
$processor=$awake.Body.GetILProcessor()
$call=$processor.Create([Mono.Cecil.Cil.OpCodes]::Call,$assembly.MainModule.ImportReference($attach))
$processor.InsertBefore($awake.Body.Instructions[0],$call)
$hide=$assembly.MainModule.Types | Where-Object Name -eq 'AvatarHideHandler'
$setHide=$hide.Methods | Where-Object Name -eq 'SetHide'
$animator=$hide.Fields | Where-Object Name -eq 'animator'
$il=$setHide.Body.GetILProcessor();$first=$setHide.Body.Instructions[0]
$il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
$il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ldfld,$animator))
$il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Brtrue,$first))
$il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
$language=$assembly.MainModule.Types | Where-Object Name -eq 'LanguageDropdownHandler'
$languageStart=$language.Methods | Where-Object Name -eq 'Start'
$languageStart.Name='SolaInitializeLanguage'
$newStart=[Mono.Cecil.MethodDefinition]::new('Start',$languageStart.Attributes,$languageStart.ReturnType)
$language.Methods.Add($newStart)
$languageInit=($support.MainModule.Types | Where-Object Name -eq 'SolaSupport').Methods | Where-Object Name -eq 'InitializeLanguage'
$newIL=$newStart.Body.GetILProcessor()
$newIL.Append($newIL.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
$newIL.Append($newIL.Create([Mono.Cecil.Cil.OpCodes]::Call,$assembly.MainModule.ImportReference($languageInit)))
$newIL.Append($newIL.Create([Mono.Cecil.Cil.OpCodes]::Ret))
$window=$assembly.MainModule.Types | Where-Object Name -eq 'AvatarWindowHandler'
$occluders=$window.Methods | Where-Object Name -eq 'SetOtherQuadsActive'
$occluders.Body.Instructions.Clear();$occluders.Body.Variables.Clear();$occluders.Body.ExceptionHandlers.Clear()
$occluderSupport=($support.MainModule.Types | Where-Object Name -eq 'SolaSupport').Methods | Where-Object Name -eq 'SetOccluderVisibility'
$ocIL=$occluders.Body.GetILProcessor()
$ocIL.Append($ocIL.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
$ocIL.Append($ocIL.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
$ocIL.Append($ocIL.Create([Mono.Cecil.Cil.OpCodes]::Call,$assembly.MainModule.ImportReference($occluderSupport)))
$ocIL.Append($ocIL.Create([Mono.Cecil.Cil.OpCodes]::Ret))
$menuType=$assembly.MainModule.Types | Where-Object Name -eq 'MenuActions'
$blocked=$menuType.Methods | Where-Object Name -eq 'IsMovementBlocked'
$quickOpen=($support.MainModule.Types | Where-Object Name -eq 'SolaDisplay').Methods | Where-Object Name -eq 'QuickMenuOpen'
$menuIL=$blocked.Body.GetILProcessor();$menuFirst=$blocked.Body.Instructions[0]
$menuIL.InsertBefore($menuFirst,$menuIL.Create([Mono.Cecil.Cil.OpCodes]::Call,$assembly.MainModule.ImportReference($quickOpen)))
$menuIL.InsertBefore($menuFirst,$menuIL.Create([Mono.Cecil.Cil.OpCodes]::Brfalse,$menuFirst))
$menuIL.InsertBefore($menuFirst,$menuIL.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1))
$menuIL.InsertBefore($menuFirst,$menuIL.Create([Mono.Cecil.Cil.OpCodes]::Ret))
$statsType=$assembly.MainModule.Types | Where-Object Name -eq 'RuntimeModelStats'
$armatureCheck=$statsType.Methods | Where-Object Name -eq 'HasProperArmature'
$properArmature=($support.MainModule.Types | Where-Object Name -eq 'SolaDisplay').Methods | Where-Object Name -eq 'ProperArmature'
$armatureCheck.Body.Instructions.Clear();$armatureCheck.Body.Variables.Clear();$armatureCheck.Body.ExceptionHandlers.Clear()
$armIL=$armatureCheck.Body.GetILProcessor()
$armIL.Append($armIL.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
$armIL.Append($armIL.Create([Mono.Cecil.Cil.OpCodes]::Call,$assembly.MainModule.ImportReference($properArmature)))
$armIL.Append($armIL.Create([Mono.Cecil.Cil.OpCodes]::Ret))
$assembly.Write($dll);$assembly.Dispose();$support.Dispose()
$postPath=Join-Path $managed 'Unity.Postprocessing.Runtime.dll'
$postBackup=Join-Path $PSScriptRoot 'Unity.Postprocessing.Runtime.original.dll'
if(!(Test-Path -LiteralPath $postBackup)){Copy-Item -LiteralPath $postPath -Destination $postBackup}
$post=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($postBackup,$reader)
# ColorKey requires every background pixel to have identical RGB. PPv2 dithering
# perturbs about 26% of alpha-zero pixels, which Windows displays as snow.
# Skip only the dithering pass; retain bloom, AO, grading and other public features.
$dithering=($post.MainModule.Types | Where-Object Name -eq 'Dithering').Methods | Where-Object Name -eq 'Render'
$dithering.Body.Instructions.Clear();$dithering.Body.Variables.Clear();$dithering.Body.ExceptionHandlers.Clear()
$ditherIL=$dithering.Body.GetILProcessor();$ditherIL.Append($ditherIL.Create([Mono.Cecil.Cil.OpCodes]::Ret))
$post.Write($postPath);$post.Dispose()
Write-Output 'Sola compatibility fixes installed. Probe commands require --sola-diagnostics.'
