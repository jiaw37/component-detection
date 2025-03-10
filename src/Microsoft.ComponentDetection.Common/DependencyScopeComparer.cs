﻿using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.ComponentDetection.Contracts.BcdeModels;

namespace Microsoft.ComponentDetection.Common
{
    /// <summary>
    /// Merges dependnecy Scope in their order of Priority. 
    /// Higher priority scope, as indicated by its lower enum value is given precendence. 
    /// </summary>
    public class DependencyScopeComparer
    {
        public static DependencyScope? GetMergedDependencyScope(DependencyScope? scope1, DependencyScope? scope2) {
            if (!scope1.HasValue)
            {
                return scope2;
            }
            else if (!scope2.HasValue)
            {
                return scope1;
            }

            return (int)scope1 < (int)scope2 ? scope1 : scope2;
        }
    }
}
