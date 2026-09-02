# Generates every .ico this app ships:
#
#   - the seven TRAY icons, a bold letter H coloured by state, drawn here from a font; and
#   - app.ico, the PRODUCT icon, converted from the same 1024px master the Mac app uses.
#
# Those are two different jobs and it is worth being clear why one script does both. They share the
# ICO container writer at the bottom of this file - System.Drawing can save a single Icon but cannot
# assemble a multi-size one, so that code had to be written by hand once - and they share the rule
# that the result is committed rather than built. Splitting them would mean a contributor has to
# know to run two scripts to refresh the icons.
#
# WHY THIS IS STILL NOT A BUILD STEP. It used to draw Segoe Fluent Icons glyphs, a Windows 11 CLIENT
# font that CI's windows-latest (Windows Server) does not ship - and GDI+ substitutes silently when a
# font is missing rather than failing, so a build agent without it would have produced seven blank
# icons with nothing going red. Segoe UI removes that specific hazard, since it is on every Windows.
#
# The files stay checked in anyway, for a different and weaker reason: an icon is judged by looking
# at it. Regenerating on every build would let a bad-looking change ship unseen. Run this by hand,
# look at the result at 16px, and commit it.
#
# Run:      powershell -File Windows/Hades.Shell/Icons/generate-icons.ps1
# Requires: any Windows (Segoe UI).

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = $PSScriptRoot

# THE TRAY AND THE WINDOW NOW SHOW DIFFERENT THINGS, deliberately. StatusGlyph.cs still maps each
# state to a Segoe Fluent pictogram for the in-window indicator, where there is room to be
# descriptive and the app is already identified by its own window. The tray has neither: it is one
# 16px square among a dozen, so it carries the product mark and expresses state in colour. These two
# no longer share codepoints and are not meant to.
#
# WHY COLOUR, when the Mac's StatusIcon is monochrome. macOS tints menu-bar icons to suit the
# current appearance automatically; Windows does not tint tray icons at all, so a single monochrome
# set is legible on exactly one theme. Rendered and compared at 16px on both a light and a dark
# taskbar: white-fill-with-dark-stroke reads well on dark and hollow on light, dark-fill-with-light-
# stroke is the mirror of that, and semantic colour is the only one of the three that reads on both.
# This stays a fixed one-to-one state -> picture mapping and decides nothing: the core already
# resolved which state this is.
#
# THE MARK IS THE LETTER H, not a per-state pictogram. A tray sits among a dozen other icons and the
# first job of this one is to be recognisably Hades; a generic circle or plug is not. State is
# carried entirely by COLOUR and by solid-versus-outline.
#
# WHAT THAT COST, and how it is paid for. The previous set distinguished states by SHAPE as well as
# hue, which let three of them share the same neutral grey. A single letter removes the shape
# dimension, so those three - idle, unknown and notRunning - would have become pixel-identical.
# Rather than ship three states that look the same:
#
#   - notRunning is drawn HOLLOW (outline only). Solid versus outline survives 16px in a way that
#     two similar greys does not, and it mirrors the distinction the Mac already draws between
#     idle's circle and notRunning's circle.dotted.
#   - unknown gets its own hue. It never meant "neutral" - it means this build does not recognise
#     what the core reported, which is an anomaly and should not look like a resting state.
#
# WHY A LETTER ALSO REMOVES A DEPENDENCY: Segoe UI ships with every Windows, where Segoe Fluent
# Icons is Windows 11 client only. That was the entire reason this script cannot be a build step
# (see the header). The .ico files stay checked in regardless - a change here still wants an eye on
# the result - but the font is no longer a reason it must be.
$states = [ordered]@{
  'idle'      = @{ Colour = @(120, 120, 120); Hollow = $false }  # neutral: nothing is happening
  'indexing'  = @{ Colour = @(0, 120, 212);   Hollow = $false }  # accent blue: work in progress
  'attached'  = @{ Colour = @(16, 137, 62);   Hollow = $false }  # green: connected and healthy
  'leaseHeld' = @{ Colour = @(202, 128, 0);   Hollow = $false }  # amber: held
  'error'     = @{ Colour = @(196, 43, 28);   Hollow = $false }  # red: something is wrong
  'unknown'   = @{ Colour = @(136, 74, 176);  Hollow = $false }  # violet: an anomaly, not a rest state

  # A SEVENTH, beyond the six ControlIconState members the plan lists. MenuContent has three
  # supervision-only cases with no core to ask. Without it the tray cannot tell "no core is running"
  # from "a core is running and has nothing to do" - precisely the distinction the ownership footer
  # exists to make unambiguous. Restarting and Failed reuse indexing and error.
  'notRunning' = @{ Colour = @(120, 120, 120); Hollow = $true }
}

# The notification area picks by DPI: 16 at 100%, 20 at 125%, 24 at 150%, 32 at 200%. Shipping only
# one size makes the icon visibly resampled on any scaled display, which is most of them.
$sizes = @(16, 20, 24, 32, 48)

$fontFamily = 'Segoe UI'
if (-not (New-Object System.Drawing.FontFamily $fontFamily)) { throw "$fontFamily is not installed" }

function New-GlyphBitmap([int]$size, [System.Drawing.Color]$colour, [bool]$hollow) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)

  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $ff = New-Object System.Drawing.FontFamily $fontFamily
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = [System.Drawing.StringAlignment]::Center
  $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center

  # A LETTER NEEDS MORE EM THAN A PICTOGRAM - a Segoe Fluent glyph fills its em box, a capital H
  # only its cap height - but not as much as it first seems. 0.95 was chosen by rendering 0.95,
  # 1.05 and 1.15 at 16px and looking at them: 1.15 is CLIPPED, its stems flattened against the top
  # and bottom edges, and 1.05 touches them. 0.95 keeps a pixel of air on every side, which is what
  # stops this reading as heavier than the icons either side of it in the tray.
  $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
  $path.AddString('H', $ff, [int][System.Drawing.FontStyle]::Bold, [float]($size * 0.95), $rect, $fmt)

  if ($hollow) {
    # Outline only. Scaled with the icon so it stays a visible ring at 16 and does not turn into a
    # heavy slab at 48.
    $pen = New-Object System.Drawing.Pen $colour, ([float]([Math]::Max(1.25, $size * 0.09)))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $path)
    $pen.Dispose()
  }
  else {
    $brush = New-Object System.Drawing.SolidBrush $colour
    $g.FillPath($brush, $path)
    $brush.Dispose()
  }

  $fmt.Dispose(); $ff.Dispose(); $path.Dispose(); $g.Dispose()
  return $bmp
}

function ConvertTo-IconDib([System.Drawing.Bitmap]$bmp) {
  # A 32bpp BMP/DIB icon image: BITMAPINFOHEADER, then the BGRA pixels BOTTOM-UP, then the AND mask.
  #
  # NOT PNG-compressed, though Windows Explorer has accepted PNG inside .ico since Vista and the
  # first version of this script wrote exactly that. GDI+ does not: System.Drawing.Icon.ToBitmap()
  # reads a PNG entry's bytes as though they were a DIB and renders pure noise. That matters here
  # rather than being a trivium, because NotifyIcon.Icon IS a System.Drawing.Icon - the PNG version
  # of these files put six panels of coloured static on screen, which is how it was caught.
  $w = $bmp.Width; $h = $bmp.Height
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter $ms

  $bw.Write([UInt32]40)        # biSize
  $bw.Write([Int32]$w)         # biWidth
  $bw.Write([Int32]($h * 2))   # biHeight: XOR image plus AND mask, per the ICO format
  $bw.Write([UInt16]1)         # biPlanes
  $bw.Write([UInt16]32)        # biBitCount
  $bw.Write([UInt32]0)         # biCompression = BI_RGB
  $bw.Write([UInt32]($w * $h * 4))
  $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)

  # Row-at-a-time via LockBits rather than GetPixel per pixel. The tray glyphs top out at 48px and
  # a per-pixel loop was fine for them; app.ico carries a 256px entry, and 65,536 GetPixel calls
  # through PowerShell's method-dispatch is tens of seconds on its own. Format32bppArgb is laid out
  # in memory as B,G,R,A - byte-for-byte what the DIB wants - and is NOT premultiplied, which
  # Format32bppPArgb would have been.
  $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
  $locked = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  try {
    $row = New-Object Byte[] ($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
      # Scan0 addresses row 0 whichever way the stride runs, so this is correct for both.
      $src = [IntPtr]::Add($locked.Scan0, $y * $locked.Stride)
      [System.Runtime.InteropServices.Marshal]::Copy($src, $row, 0, $row.Length)
      $bw.Write($row)
    }
  }
  finally { $bmp.UnlockBits($locked) }

  # AND mask: zeroed, because the 32bpp alpha channel already carries transparency. It is still
  # required to be present and correctly sized - each row padded to a 4-byte boundary.
  $maskStride = [Math]::Floor(($w + 31) / 32) * 4
  for ($y = 0; $y -lt $h; $y++) { $bw.Write((New-Object Byte[] $maskStride)) }

  $bw.Flush()
  $bytes = $ms.ToArray()
  $bw.Dispose(); $ms.Dispose()
  return , $bytes
}

function Write-Ico([string]$path, [hashtable]$imagesBySize) {
  # ICO container, written by hand: System.Drawing can save a single Icon but cannot assemble a
  # multi-size one.
  $ordered = $imagesBySize.Keys | Sort-Object
  $count = $ordered.Count
  $fs = [System.IO.File]::Create($path)
  $bw = New-Object System.IO.BinaryWriter $fs

  $bw.Write([UInt16]0)      # reserved
  $bw.Write([UInt16]1)      # type: 1 = icon
  $bw.Write([UInt16]$count)

  $offset = 6 + (16 * $count)
  foreach ($s in $ordered) {
    $data = $imagesBySize[$s]
    # 0 means 256 in this field; every size we ship is smaller, but keep the rule explicit.
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)   # width
    $bw.Write([Byte]$dim)   # height
    $bw.Write([Byte]0)      # palette count (0 = no palette)
    $bw.Write([Byte]0)      # reserved
    $bw.Write([UInt16]1)    # colour planes
    $bw.Write([UInt16]32)   # bits per pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
  }
  foreach ($s in $ordered) { $bw.Write($imagesBySize[$s]) }

  $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

foreach ($name in $states.Keys) {
  $rgb = $states[$name].Colour
  $hollow = $states[$name].Hollow
  $colour = [System.Drawing.Color]::FromArgb(255, $rgb[0], $rgb[1], $rgb[2])
  $images = @{}
  foreach ($s in $sizes) {
    $b = New-GlyphBitmap $s $colour $hollow
    $images[$s] = ConvertTo-IconDib $b
    $b.Dispose()
  }
  $target = Join-Path $outDir "$name.ico"
  Write-Ico $target $images
  Write-Output ("{0,-11} rgb({1,3},{2,3},{3,3})  {4,-6}  {5} sizes  {6} bytes" -f `
    $name, $rgb[0], $rgb[1], $rgb[2], $(if ($hollow) { 'hollow' } else { 'solid' }), $sizes.Count, (Get-Item $target).Length)
}

# =================================================================================================
# THE PRODUCT ICON. Everything above is STATE; this is IDENTITY - what Windows shows in the Start
# menu, the taskbar, Alt+Tab, Explorer, the window title bar and Add/Remove Programs.
#
# WHY IT EXISTS AT ALL: it did not, and that was a hole. ApplicationIcon was absent from
# Hades.Shell.csproj, so the executable carried .NET's generic default and every one of those
# surfaces showed a blank page with a blue square. The Mac never had this problem because
# build-app.sh generates AppIcon.icns from Resources/AppIcon-1024.png; Windows simply had no
# equivalent step, and the plan's icon task (Task 3 Step 6) only ever covered the tray. This is it.
#
# THE SAME ARTWORK AS THE MAC, read from the Mac's own Resources directory rather than copied into
# this one. build-app.sh states the reason it keeps a single master - "one source of truth, and
# replacing the icon is dropping in a new PNG" - and duplicating the PNG under Windows/ would break
# that promise the first time either copy was updated. Nothing at BUILD time reaches across the
# platform folders: this script is run by hand and app.ico is committed beside the tray icons.
#
# CONVERTED FROM THE FULL-BLEED SOURCE, NOT THE SHIPPED MAC PNG, which is the one deliberate
# difference between the two platforms' icons. AppIcon-1024.png has macOS's icon grid baked in:
# measured, its artwork starts 100px into a 1024px canvas - a 9.8% transparent margin on every
# side. macOS needs that because it composites icons into a larger box. Windows adds no padding of
# its own, so that file used as-is draws a mark filling 80% of its box beside neighbours that fill
# theirs: at 16px, about 13 usable pixels instead of 16. All three options were rendered at
# 16/24/32/48 on both a light and a dark background and looked at - the as-is master is visibly the
# smallest and dimmest, and its node graph is the first to turn to smudge.
#
# So: the full-bleed source, masked to the SAME rounded square at the SAME 22% corner radius the
# Mac icon uses, scaled to fill the canvas rather than sit inside a margin. Same mark, same
# silhouette, sized for the platform drawing it. (A tighter 14-18% radius is the more Windows-native
# value and was compared at 16/32/48; below 32px the difference is under two pixels and invisible,
# and matching the Mac exactly is worth more than matching a convention nobody will measure.)

# The shell picks an entry by surface and DPI and does NOT interpolate between them - it takes the
# nearest and resamples, so a missing size is a soft icon rather than a missing one. 16 through 48
# cover the taskbar, Start menu and Explorer's small views across 100-250% scaling; 64/128/256
# cover Explorer's large and extra-large views and the high-DPI Alt+Tab card.
$appSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# WHY THIS FILE IS ~360 KB: every entry is an uncompressed DIB. PNG-compressing the 128 and 256
# entries is normal practice for .ico and would cut it to roughly a tenth - but this repo has
# already been bitten once by PNG inside .ico (see ConvertTo-IconDib's header, and the Outcome note
# for Task 3), and one rule that holds for every .ico here is worth more than 300 KB of a binary
# that changes about never.
$appMasterPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\..\Mac\HadesApp\Resources\AppIcon-source-fullbleed.png'))
if (-not (Test-Path $appMasterPath)) {
    # Loud, and fatal rather than a warning. build-app.sh can afford to warn and ship the generic
    # icon because it is building a throwaway bundle; here the same mistake would leave a good
    # committed app.ico in place while reporting success, which is worse than stopping.
    throw "generate-icons.ps1: no icon master at $appMasterPath"
}

function New-ProductBitmap([System.Drawing.Image]$master, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Resample first, into its own bitmap at exactly the target size, so the brush below samples a
    # correctly filtered image rather than the 1024px original.
    $scaled = New-Object System.Drawing.Bitmap $size, $size
    $sg = [System.Drawing.Graphics]::FromImage($scaled)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.DrawImage($master, 0, 0, [float]$size, [float]$size)
    $sg.Dispose()

    # Four arcs and a close: the rounded square, radius 22% of the side, drawn edge to edge.
    $side = [float]$size
    $d = [float]($side * 0.22 * 2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($side - $d, 0, $d, $d, 270, 90)
    $path.AddArc($side - $d, $side - $d, $d, $d, 0, 90)
    $path.AddArc(0, $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # FillPath with a TextureBrush, NOT SetClip then DrawImage. GDI+ clipping regions are not
    # antialiased - clipping would give the corners a hard staircase, which at 16 and 20px is most
    # of what you see of the shape. FillPath honours SmoothingMode. TileFlipXY rather than Clamp so
    # the antialiased boundary samples a mirrored edge instead of transparent black, which would
    # darken the outermost pixel all the way round.
    $brush = New-Object System.Drawing.TextureBrush $scaled
    $brush.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    $g.FillPath($brush, $path)

    $brush.Dispose(); $path.Dispose(); $scaled.Dispose(); $g.Dispose()
    return $bmp
}

$appImages = @{}
$master = [System.Drawing.Image]::FromFile($appMasterPath)
try {
    foreach ($s in $appSizes) {
        $b = New-ProductBitmap $master $s
        $appImages[$s] = ConvertTo-IconDib $b
        $b.Dispose()
    }
}
finally { $master.Dispose() }

$appTarget = Join-Path $outDir 'app.ico'
Write-Ico $appTarget $appImages
Write-Output ("{0,-11} {1}  {2} sizes  {3:N0} bytes" -f `
    'app', (Split-Path $appMasterPath -Leaf), $appSizes.Count, (Get-Item $appTarget).Length)
