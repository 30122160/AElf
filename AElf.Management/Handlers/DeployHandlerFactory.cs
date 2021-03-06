﻿using System;
using AElf.Configuration.Config.Management;

namespace AElf.Management.Handlers
{
    public class DeployHandlerFactory
    {
        public static IDeployHandler GetHandler()
        {
            var type = DeployConfig.Instance.Type.ToLower();
            switch (type)
            {
                case "k8s":
                    return K8SDeployHandler.Instance;
                default:
                    throw new ArgumentException("deploy type is incorrect");
            }
        }

    }
}