# ICCMF

**ISO CORE Community Modding Framework**

## Abbreviation

| Letter | Meaning |
|:---:|---|
| **I** | Iso |
| **C** | Core |
| **C** | Community |
| **M** | Modding |
| **F** | Framework |

## Terminal Commands

### Update

```bash
iccmf update
```

Update ICCMF and BepInEx to latest version.

### Modlist

**Add a mod**

```bash
iccmf modlist add <mod_name> <path/to/mod>
```

Add a mod to modlist.

**Update a mod**

```bash
iccmf modlist update <mod_name> <path/to/mod>
```

Remove an existing mod and replace it with mod at `/path/to/mod/`.

**Remove a mod**

```bash
iccmf modlist remove <mod_name>
```

Remove a mod.

### Create

**Create a mod**

```bash
iccmf create mod <mod_name>
```

Create a new mod template in current directory.

## Mod Manager

It will also ship with a mod manager:

- Find community and official mods
- Manage them easily
