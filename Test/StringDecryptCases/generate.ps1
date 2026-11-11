# Copyright (C) 2026- marinusmaurice@outlook.com
# Makes Data.cs and strings.bin: the encrypted strings of Program.cs. The plain strings are only here and in
# ../DecompilerStress/check.ps1, which checks that dnSpy.Console --decrypt-strings finds them all.
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Join-Bytes($bytes) { ($bytes | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', ' }

# XorStrings: one table, the key repeated over it
$key = [byte[]](0x5A, 0x13, 0xC7, 0x2E, 0x91)
$xorBlob = New-Object System.Collections.Generic.List[byte]
$xorOffsets = @(); $xorLengths = @()
foreach ($s in 'Hello from the XOR table', 'Licence key accepted') {
	$bytes = $utf8.GetBytes($s)
	$xorOffsets += $xorBlob.Count; $xorLengths += $bytes.Length
	foreach ($b in $bytes) { $i = $xorBlob.Count; $xorBlob.Add([byte]($b -bxor $key[$i % $key.Length])) }
}

# Base64Strings: base64 of reversed UTF-16
$unicode = [System.Text.Encoding]::Unicode.GetBytes('Reversed UTF-16 in base64')
[Array]::Reverse($unicode)
$base64 = [Convert]::ToBase64String($unicode)

# ResourceStrings: a count, then BinaryWriter strings with every char XOR 0x2A
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms, $utf8)
$resStrings = 'Read from an embedded resource', 'Cached in a dictionary'
$w.Write([int]$resStrings.Count)
foreach ($s in $resStrings) { $w.Write(-join ($s.ToCharArray() | ForEach-Object { [char]([int]$_ -bxor 0x2A) })) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'strings.bin'), $ms.ToArray())

# GenericStrings: a ushort pool, each char XOR (0x3C1 + index * 7); id = ((start << 12) | length) ^ 0x5EED1234
$pool = @(); $ids = @()
foreach ($s in 'Generic Get<string>(uint)', 'Set up by a module initializer') {
	$start = $pool.Count
	foreach ($c in $s.ToCharArray()) { $i = $pool.Count; $pool += (([int]$c) -bxor ((0x3C1 + $i * 7) -band 0xFFFF)) }
	$ids += ((($start -shl 12) -bor $s.Length) -bxor 0x5EED1234)
}

# AesStrings: AES-256-CBC, zero IV, key = SHA-256("dnSpy test key")
$aes = [System.Security.Cryptography.Aes]::Create()
$aes.Key = [System.Security.Cryptography.SHA256]::Create().ComputeHash($utf8.GetBytes('dnSpy test key'))
$aes.IV = New-Object byte[] 16
$enc = $aes.CreateEncryptor()
$aesBlob = New-Object System.Collections.Generic.List[byte]
$aesOffsets = @(); $aesLengths = @()
foreach ($s in @('Decrypted with AES-256')) {
	$plain = $utf8.GetBytes($s)
	$cipher = $enc.TransformFinalBlock($plain, 0, $plain.Length)
	$aesOffsets += $aesBlob.Count; $aesLengths += $cipher.Length
	$aesBlob.AddRange([byte[]]$cipher)
}

# RotorStrings: letters shifted by 3 (Rotor(3)) and by 10 (DoubleRotor(5) shifts twice)
function Rotate($s, $n) {
	-join ($s.ToCharArray() | ForEach-Object {
		$c = [int]$_
		if ($c -ge 97 -and $c -le 122) { [char](97 + ($c - 97 + $n) % 26) }
		elseif ($c -ge 65 -and $c -le 90) { [char](65 + ($c - 65 + $n) % 26) }
		else { [char]$c }
	})
}
$rotated = @((Rotate 'Caesar shift by three' 3), (Rotate 'Virtual override shifts ten' 10))

$cs = @"
/*
    Copyright (C) 2026- marinusmaurice@outlook.com
*/

// Made by generate.ps1
namespace StringDecryptCases {
	static class Data {
		public const string Base64Text = "$base64";
		public const uint GenericId0 = 0x$('{0:X8}' -f $ids[0]);
		public const uint GenericId1 = 0x$('{0:X8}' -f $ids[1]);
	}

	static partial class XorStrings {
		static readonly byte[] blob = { $(Join-Bytes $xorBlob) };
		static readonly int[] offsets = { $($xorOffsets -join ', ') };
		static readonly int[] lengths = { $($xorLengths -join ', ') };
	}

	static partial class GenericStrings {
		static readonly ushort[] raw = { $(($pool | ForEach-Object { '0x{0:X4}' -f $_ }) -join ', ') };
	}

	static partial class AesStrings {
		static readonly byte[] aesBlob = { $(Join-Bytes $aesBlob) };
		static readonly int[] aesOffsets = { $($aesOffsets -join ', ') };
		static readonly int[] aesLengths = { $($aesLengths -join ', ') };
	}

	static partial class RotorStrings {
		static readonly string[] rotated = { "$($rotated[0])", "$($rotated[1])" };
	}
}
"@
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'Data.cs'), $cs.Replace("`r`n", "`n").Replace("`n", "`r`n"), $utf8)
Write-Host 'Data.cs and strings.bin written'
