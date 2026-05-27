# Homebrew Cask formula for Game Master Sound Board.
#
# This file lives in the project repo for visibility but Homebrew users
# install via a tap. Two paths to ship it:
#
#   1. Submit to homebrew-cask (the main cask repo) once you have enough
#      stable releases. Subject to homebrew-cask review criteria (must be
#      open-source binary distribution, ≥30 days old, etc.).
#
#   2. Easier short-term: publish your own tap, e.g.
#         github.com/DevinSanders/homebrew-soundboard
#      Users then:
#         brew tap DevinSanders/soundboard
#         brew install --cask gmsoundboard
#
# Placeholders rewritten by the release workflow before committing to the tap:
#   $VERSION$        e.g. 1.2.3
#   $SHA256_ARM64$   SHA256 of GameMasterSoundBoard-1.2.3-osx-arm64.dmg
#   $SHA256_X64$     SHA256 of GameMasterSoundBoard-1.2.3-osx-x64.dmg

cask "gmsoundboard" do
  version "$VERSION$"

  if Hardware::CPU.arm?
    sha256 "$SHA256_ARM64$"
    url "https://github.com/DevinSanders/game-master-soundboard/releases/download/v#{version}/GameMasterSoundBoard-#{version}-osx-arm64.dmg"
  else
    sha256 "$SHA256_X64$"
    url "https://github.com/DevinSanders/game-master-soundboard/releases/download/v#{version}/GameMasterSoundBoard-#{version}-osx-x64.dmg"
  end

  name "Game Master Sound Board"
  desc "Cross-platform soundboard for tabletop RPG sessions"
  homepage "https://github.com/DevinSanders/game-master-soundboard"

  # Minimum supported macOS — keep in sync with LSMinimumSystemVersion in Info.plist.
  depends_on macos: ">= :big_sur"

  app "Game Master Sound Board.app"

  # Cleanup on uninstall: app preferences, library database, plugins, logs.
  zap trash: [
    "~/Library/Application Support/GameMasterSoundBoard",
    "~/Library/Preferences/com.gamemastersoundboard.app.plist",
    "~/Library/Caches/com.gamemastersoundboard.app",
  ]
end
