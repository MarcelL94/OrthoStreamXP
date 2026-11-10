# Packages

[User guide](README.md) › Packages

OrthoStreamXP streams photos onto scenery packages: the ground's shape (the mesh) for each area. The quickest way to get them is to download the mesh of a whole region, on the **Packages** page or in the **Scenery packages** step of the setup. You can also use packages of your own, or both together.

![The Packages page, with Australia and the Pacific installed](images/packages.png)

The meshes are downloaded from a public collection on [GitHub](https://github.com/kubilus1/autoortho-scenery). Six regions are offered:

| Region | Package | Download (with roads, forests and buildings) |
|---|---|---|
| Europe | `OSXP_Eur` | about 16 GB |
| North America | `OSXP_NA` | about 25 GB |
| South America | `OSXP_SA` | about 9 GB |
| Africa | `OSXP_Afr` | about 12 GB |
| Asia | `OSXP_Asi` | about 33 GB |
| Australia and the Pacific | `OSXP_AusPac` | about 6 GB |

The page shows the exact sizes of the current release.

## Where they go

Each region is installed as a package of its own in your **scenery packages folder**, beside your own packages. If you download one during the setup and have not chosen a folder yet, it goes to `OrthoStreamXP\Base Packages` in your user folder, and that folder becomes your scenery packages folder.

Your own packages stay **above** the downloaded regions in X-Plane's scenery list. Where both cover the same area, X-Plane shows yours.

Leave about 1.7 times the download size free on the drive: the files take more room once they are unpacked.

## Roads, forests and buildings

A square of a downloaded mesh no longer shows X-Plane's own roads, forests and buildings. **Roads, forests and buildings** downloads them with each region, into one shared package, `OSXP_Overlays`. Turn it off if another package of yours already brings them. The choice applies to the next region you install or update.

## Downloading, stopping and resuming

![A region downloading](images/packages-downloading.png)

- **Install** starts the download. While one region downloads, the others wait: one install runs at a time. The progress and speed show under the region.
- **Stop** stops it. What was downloaded is kept: **Resume** continues from there, even after OrthoStreamXP was closed.
- A region is used from OrthoStreamXP's next start, and never half-installed: it appears in your packages folder only when all of it is there.
- When a newer release comes out, the region says **Update available**. Updating downloads it again in full.
- **Delete** removes the region and its roads, forests and buildings. Another region's stay.

You can install a new region while OrthoStreamXP runs. Updating or deleting a region needs OrthoStreamXP stopped, because X-Plane has its files open while it runs.
