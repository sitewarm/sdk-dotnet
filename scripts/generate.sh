#!/usr/bin/env sh
# Regenerates src/Sitewarm.Sdk/Operations.cs and the models (src/Sitewarm.Sdk/Generated) from openapi/openapi.yaml.
# Needs Python 3 with PyYAML and Docker. Run from the repository root.
set -eu

python3 scripts/gen_operations.py
rm -rf build/gen
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD":/work -w /work \
  openapitools/openapi-generator-cli:v7.26.0 generate \
  -i build/openapi.prep.yaml -g csharp -o build/gen --library generichost \
  --global-property "models,supportingFiles,modelTests=false,modelDocs=false" \
  --additional-properties "packageName=Sitewarm.Sdk,targetFramework=net8.0,nullableReferenceTypes=true,hideGenerationTimestamp=true,sourceFolder=src,useDateTimeOffset=true,validatable=false" >/dev/null
rm -rf src/Sitewarm.Sdk/Generated
mkdir -p src/Sitewarm.Sdk/Generated
cp -R build/gen/src/Sitewarm.Sdk/Model src/Sitewarm.Sdk/Generated/Model
# Only what the models use; the generated host/DI client is not part of this SDK.
mkdir -p src/Sitewarm.Sdk/Generated/Client
for f in ClientUtils DateOnlyJsonConverter DateOnlyNullableJsonConverter DateTimeJsonConverter DateTimeNullableJsonConverter \
         FileParameter JsonSerializerOptionsProvider Option; do
  cp "build/gen/src/Sitewarm.Sdk/Client/$f.cs" src/Sitewarm.Sdk/Generated/Client/
done
echo "generated models"
