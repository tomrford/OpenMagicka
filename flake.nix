{
  # OpenMagicka with a pinned toolchain (macOS and Linux; Windows builds as the README says).
  #   nix run .                  play (set MAGICKA_DIR or pass --content <Magicka folder>)
  #   nix develop                .NET 10 SDK, cmake, ffmpeg for dotnet build/test
  # The flake reads the FNA submodules: clone with --recursive (or git submodule update --init --recursive).
  inputs = {
    self.submodules = true;
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = {
    self,
    nixpkgs,
    flake-utils,
  }:
    flake-utils.lib.eachSystem ["aarch64-darwin" "x86_64-linux" "aarch64-linux"] (
      system: let
        pkgs = import nixpkgs {inherit system;};
        inherit (pkgs) lib;
        isDarwin = pkgs.stdenv.hostPlatform.isDarwin;

        # FNA loads its natives by these names (lib/FNA/app.config).
        native = name:
          if isDarwin
          then "lib${name}.0.dylib"
          else "lib${name}.so.0";

        dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
        dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;
        ffmpeg = pkgs.ffmpeg-headless;

        # Our FNA3D (native/README.md): the submodule's FNA3D + MojoShader with our patches, as
        # tools/build_fna3d.ps1 and tools/build_fna3d_macos.sh build it, against nixpkgs' SDL3.
        # In the scripts' order: the shared patches, then the macOS ones, each by number.
        patchesIn = dir: lib.sort lib.lessThan (map toString (lib.filesystem.listFilesRecursive dir));
        mojoPatches =
          patchesIn ./native/patches/MojoShader
          ++ lib.optionals isDarwin (patchesIn ./native/patches/MojoShader-macos);
        fna3dPatches = lib.optionals isDarwin (patchesIn ./native/patches/FNA3D-macos);

        fna3d = pkgs.stdenv.mkDerivation {
          pname = "fna3d-openmagicka";
          version = "26.10";
          src = self + "/lib/FNA/lib/FNA3D";

          nativeBuildInputs = [pkgs.cmake pkgs.git] ++ lib.optionals isDarwin [pkgs.fixDarwinDylibNames];
          buildInputs = [pkgs.sdl3];

          postPatch = ''
            for patch in ${lib.escapeShellArgs mojoPatches}; do
              echo "applying MojoShader/$(basename "$patch")"
              (cd MojoShader && git apply --ignore-whitespace "$patch")
            done
            for patch in ${lib.escapeShellArgs fna3dPatches}; do
              echo "applying FNA3D/$(basename "$patch")"
              git apply --ignore-whitespace "$patch"
            done
          '';

          cmakeFlags = [
            "-DBUILD_SDL3=ON"
            "-DSDL3_INCLUDE_DIRS=${lib.getDev pkgs.sdl3}/include"
            "-DSDL3_LIBRARIES=${lib.getLib pkgs.sdl3}/lib/libSDL3${pkgs.stdenv.hostPlatform.extensions.sharedLibrary}"
          ];
        };

        openmagicka = pkgs.buildDotnetModule {
          pname = "openmagicka";
          version = "0.0.3";
          src = self;

          projectFile = "src/OpenMagicka.Launcher/OpenMagicka.Launcher.csproj";
          nugetDeps = ./nix/deps.json; # regenerate: nix build .#default.fetch-deps && ./result nix/deps.json
          inherit dotnet-sdk dotnet-runtime;

          # The repo's prebuilt natives (lib/fnalibs, native/<rid>) are left out: ours come from Nix.
          dotnetFlags = ["-p:NativeTarget=none"];
          executables = ["OpenMagicka"];
          makeWrapperArgs = ["--set-default" "OPENMAGICKA_FFMPEG" "${ffmpeg}/bin/ffmpeg"];

          # Beside the app: FNA3D, SDL3, FAudio, and FNA3D's build record with its hash, by which
          # GraphicsDriver.HasOwnFna3d knows it for ours (the effects stock FNA3D crashes on run).
          postFixup = ''
            app=$out/lib/openmagicka
            ln -s ${fna3d}/lib/${native "FNA3D"} $app/
            ln -s ${lib.getLib pkgs.sdl3}/lib/${native "SDL3"} $app/
            ln -s ${pkgs.faudio}/lib/${native "FAudio"} $app/
            {
              echo "OpenMagicka build of FNA3D (altered source: see native/README.md), built by flake.nix."
              echo "${native "FNA3D"} sha256 $(sha256sum "$app/${native "FNA3D"}" | cut -d' ' -f1)"
            } > $app/FNA3D.OpenMagicka.txt
          '';

          meta.mainProgram = "OpenMagicka";
        };
      in {
        packages = {
          default = openmagicka;
          inherit openmagicka fna3d;
        };

        apps.default = flake-utils.lib.mkApp {drv = openmagicka;};

        devShells.default = pkgs.mkShell {
          packages = [dotnet-sdk pkgs.cmake ffmpeg pkgs.gh pkgs.git];
          DOTNET_ROOT = "${dotnet-sdk}/share/dotnet";
          OPENMAGICKA_FFMPEG = "${ffmpeg}/bin/ffmpeg";
        };
      }
    );
}
