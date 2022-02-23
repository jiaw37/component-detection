using System.Collections.Generic;
using System.Linq;
using CycloneDX.Json;
using CycloneDX.Models;
using Microsoft.ComponentDetection.Contracts.BcdeModels;

namespace Microsoft.ComponentDetection.Contracts.Mappers
{
    public static class CycloneDx
    {
        public static string ToCycloneDx(this ScanResult scanResult)
        {
            return Serializer.Serialize(
                new Bom
                {
                    Metadata = new Metadata
                    {
                        Tools = new List<Tool>
                        {
                            new Tool
                            {
                                Vendor = "Microsoft",
                                Name = "Component Detection",
                            },
                        },
                    },
                    Components = scanResult.ComponentsFound.ToComponents(),
                });
        }

        private static Component ToComponent(this ScannedComponent scannedComponent)
        {
            return new Component
            {
                Type = Component.Classification.Library,
                Name = scannedComponent.Component.PackageUrl.Name,
                Version = scannedComponent.Component.PackageUrl.Version,
                Purl = scannedComponent.Component.PackageUrl.ToString(),
                Properties = scannedComponent.GenerateProperties(),
            };
        }

        private static List<Component> ToComponents(this IEnumerable<ScannedComponent> scannedComponents)
        {
            return scannedComponents.Select(sc => sc.ToComponent()).ToList();
        }

        private static List<Property> GenerateProperties(this ScannedComponent scannedComponent)
        {
            var properties = new List<Property>();
            properties.AddRange(scannedComponent.LocationsFoundAt.Select((locationFoundAt, i) => new Property
            {
                Name = $"component-detection:location:{i}",
                Value = locationFoundAt,
            }));
            return properties;
        }
    }
}
