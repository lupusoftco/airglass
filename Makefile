APP      = AirGlass
CONFIG  ?= Debug
DERIVED  = build
APP_PATH = $(DERIVED)/Build/Products/$(CONFIG)/$(APP).app

.PHONY: project build run clean

project:
	xcodegen generate

build: project
	xcodebuild -project $(APP).xcodeproj -scheme $(APP) -configuration $(CONFIG) \
		-derivedDataPath $(DERIVED) -allowProvisioningUpdates build

run: build
	-pkill -x $(APP)
	open "$(APP_PATH)"

clean:
	rm -rf $(DERIVED) $(APP).xcodeproj
