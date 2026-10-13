interface RouteGenerationState {
  isGenerate: boolean
  generateRoutesAtFront: (routes: []) => void
}

export function settleRouteGenerationFailure(routeStore: RouteGenerationState) {
  if (!routeStore.isGenerate) {
    routeStore.generateRoutesAtFront([])
  }
}
